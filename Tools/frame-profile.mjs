import { prepareDiagnostics } from './diagnostics.mjs';
import { spawn } from 'node:child_process';
import { mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

const project = path.resolve(process.env.SLACK_PROFILE_PROJECT ?? path.join(import.meta.dirname, '..'));
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const options = {};
for (let i = 2; i < process.argv.length; i++) {
  const arg = process.argv[i];
  if (!arg.startsWith('--')) continue;
  const split = arg.slice(2).split('=');
  if (split.length > 1) options[split[0]] = split.slice(1).join('=');
  else if (process.argv[i + 1] && !process.argv[i + 1].startsWith('--')) options[split[0]] = process.argv[++i];
  else options[split[0]] = true;
}
const label = String(options.label ?? 'baseline');
const seconds = Number(options.seconds ?? 120);
const warmup = Number(options.warmup ?? 3);
const output = path.resolve(options.out ?? path.join(project, '.playtest', 'profile'));
const runRoot = path.join(output, label);
const thresholdMs = Number(options.threshold ?? (1000 / 60));
const targetPresentHz = Number(options.targetHz ?? 60);
const replay = typeof options.replay === 'string' ? options.replay : null;
const expectedRoom = Number(options.room ?? 11);
const entryRoom = Number(options.entryRoom ?? 10);
const requestedRoom = options.room === undefined ? null : Number(options.room);
const outlierMultiplier = Number(options.iqr ?? 3);
const minSamples = Number(options.minSamples ?? 3);
const fullscreen = options.windowed !== true;
const minValidRatio = Number(options.minValidRatio ?? 0.5);
const allowInvalid = options['allow-invalid'] === true;

function percentile(values, p) {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(sorted.length * p) - 1))];
}
function describe(values) {
  if (values.length === 0) return null;
  return {
    count: values.length,
    meanMs: values.reduce((a, b) => a + b, 0) / values.length,
    p50Ms: percentile(values, 0.5),
    p95Ms: percentile(values, 0.95),
    p99Ms: percentile(values, 0.99),
    maxMs: Math.max(...values)
  };
}
function regression(series) {
  const n = series.length;
  if (n < 2) return null;
  const meanX = (n - 1) / 2;
  const meanY = series.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0, syy = 0;
  series.forEach((y, x) => {
    sxy += (x - meanX) * (y - meanY);
    sxx += (x - meanX) ** 2;
    syy += (y - meanY) ** 2;
  });
  const slope = sxx === 0 ? 0 : sxy / sxx;
  return { slope, intercept: meanY - slope * meanX, r2: sxx === 0 || syy === 0 ? 0 : (sxy * sxy) / (sxx * syy) };
}
function pearson(a, b) {
  const n = Math.min(a.length, b.length);
  if (n < 2) return null;
  const meanA = a.slice(0, n).reduce((x, y) => x + y, 0) / n;
  const meanB = b.slice(0, n).reduce((x, y) => x + y, 0) / n;
  let num = 0, da = 0, db = 0;
  for (let i = 0; i < n; i++) {
    const pa = a[i] - meanA, pb = b[i] - meanB;
    num += pa * pb;
    da += pa * pa;
    db += pb * pb;
  }
  return { r: da === 0 || db === 0 ? 0 : num / Math.sqrt(da * db), count: n };
}
function pairedCorrelation(rows, xOf, yOf) {
  const xs = [], ys = [];
  for (const row of rows) {
    const x = xOf(row), y = yOf(row);
    if (typeof x !== 'number' || !Number.isFinite(x) || typeof y !== 'number' || !Number.isFinite(y)) continue;
    xs.push(x);
    ys.push(y);
  }
  const result = pearson(xs, ys);
  return result === null ? null : { r: result.r, n: result.count };
}
function correlationReport(rows, allRows, name, xOf, yOf) {
  const primary = pairedCorrelation(rows, xOf, yOf);
  const variants = [];
  const withWarmup = pairedCorrelation(allRows, xOf, yOf);
  if (withWarmup !== null) variants.push({ label: '包含 warmup 窗口', n: withWarmup.n, r: withWarmup.r });
  const dropFirst = rows.slice(1);
  const withoutFirst = pairedCorrelation(dropFirst, xOf, yOf);
  if (withoutFirst !== null) variants.push({ label: '剔除首个 steady 窗口', n: withoutFirst.n, r: withoutFirst.r });
  const drops = [1, 2, 3, 5];
  for (const drop of drops) {
    const trimmed = rows.slice(0, rows.length - drop);
    const variant = pairedCorrelation(trimmed, xOf, yOf);
    if (variant !== null) variants.push({ label: '剔除末 ' + drop + ' 窗口', n: variant.n, r: variant.r });
  }
  let spread = null;
  if (variants.length > 0 && primary !== null) {
    const values = variants.map(entry => entry.r).concat([primary.r]);
    spread = { min: Math.min(...values), max: Math.max(...values), range: Math.max(...values) - Math.min(...values) };
  }
  const signStable = spread === null ? null : spread.min < 0 === spread.max < 0;
  return {
    name,
    r: primary === null ? null : primary.r,
    n: primary === null ? null : primary.n,
    sampleRange: 'steady 窗口（已剔除前 ' + warmup + 's warmup）',
    excludedOutliers: 0,
    sensitivity: {
      variants,
      spread,
      signStable,
      note: spread === null ? null : signStable ? '所有口径符号一致' : '符号随口径翻转，结论不可靠'
    },
    caveat: '相关系数必须连同 n 与样本范围一起解读；单点离群窗口可显著改变 r'
  };
}
function fences(values, multiplier) {
  if (values.length < 4) return null;
  const q1 = percentile(values, 0.25), q3 = percentile(values, 0.75), iqr = q3 - q1;
  if (iqr <= 0) return null;
  return { q1, q3, iqr, high: q3 + multiplier * iqr, low: q1 - multiplier * iqr };
}
function splitOutliers(rows, valueOf) {
  const values = rows.map(valueOf).filter(value => typeof value === 'number' && Number.isFinite(value));
  const limits = fences(values, outlierMultiplier);
  if (limits === null) return { limits: null, kept: rows, outliers: [] };
  const kept = [], outliers = [];
  for (const row of rows) {
    const value = valueOf(row);
    if (typeof value !== 'number' || !Number.isFinite(value) || value > limits.high || value < limits.low) outliers.push(row);
    else kept.push(row);
  }
  return { limits, kept, outliers };
}
function checkBuckets(rows) {
  const violations = [];
  let present = 0;
  const probes = [['phases', 'logicSteps', 'per-step'], ['processPhases', 'process.count', 'per-callback'], ['drawPhases', 'present.count', 'per-draw']];
  const checked = [];
  for (const [field, denominator, label] of probes) {
    let windowsChecked = 0, entriesChecked = 0;
    for (const row of rows) {
      const bucket = row.record[field];
      if (!bucket) continue;
      const expected = denominator === 'logicSteps' ? row.record.logicSteps : denominator === 'process.count' ? row.record.process?.count : row.record.present?.count;
      if (typeof expected !== 'number') continue;
      windowsChecked++;
      for (const [name, entry] of Object.entries(bucket)) {
        entriesChecked++;
        if (entry.count !== expected) violations.push({ field, probe: name, label, expected, actual: entry.count, frame: row.frame });
      }
    }
    present += windowsChecked;
    checked.push({ field, denominator, windowsChecked, entriesChecked });
  }
  return {
    ok: violations.length === 0,
    decidable: present > 0,
    checked,
    violations: violations.slice(0, 20),
    violationCount: violations.length,
    note: '三桶不变量：phases.count==logicSteps / processPhases.count==process.count / drawPhases.count==present.count；任一不成立 = 探针放错层'
  };
}
function aliveInstances(record) {
  if (record.alive && typeof record.alive.instances === 'number') return { value: record.alive.instances, source: 'alive.instances' };
  if (Array.isArray(record.objectProfile?.objects)) {
    const total = record.objectProfile.objects.reduce((sum, entry) => sum + (typeof entry?.activeInstances === 'number' ? entry.activeInstances : 0), 0);
    return { value: total, source: 'objectProfile.activeInstances' };
  }
  return { value: null, source: 'absent' };
}
function rendersOnScreen(record) {
  const intervalCount = record.renderInterval?.count ?? 0;
  const completedFps = record.completedRenderFps ?? 0;
  return intervalCount > 0 && completedFps > 0;
}
function nonNull(values) {
  return values.filter(value => typeof value === 'number' && Number.isFinite(value));
}
function mean(values) {
  return values.length === 0 ? null : values.reduce((a, b) => a + b, 0) / values.length;
}

async function newestPerformanceLog(root) {
  const directory = path.join(root, 'user', 'logs');
  let names = [];
  try { names = await readdir(directory); } catch { return null; }
  const files = names.filter(name => name.startsWith('performance-') && name.endsWith('.jsonl'));
  if (files.length === 0) return null;
  let best = null;
  for (const name of files) {
    const full = path.join(directory, name);
    const info = await stat(full);
    if (best === null || info.mtimeMs > best.mtimeMs) best = { path: full, mtimeMs: info.mtimeMs };
  }
  return best.path;
}

async function firstCadenceRoom(root) {
  const log = await newestPerformanceLog(root);
  if (log === null) return { log: null, room: null };
  let text = '';
  try { text = await readFile(log, 'utf8'); } catch { return { log, room: null }; }
  for (const line of text.split('\n')) {
    if (line.trim().length === 0) continue;
    let record = null;
    try { record = JSON.parse(line); } catch { continue; }
    if (record.type === 'cadence') return { log, room: record.room };
  }
  return { log, room: null };
}

async function capture() {
  prepareDiagnostics(project);
  await rm(runRoot, { recursive: true, force: true });
  await mkdir(runRoot, { recursive: true });
  const frames = Math.ceil((seconds + 12) * 30);
  const objects = process.env.SLACK_PROFILE_OBJECTS !== '0';
  const logFile = path.join(runRoot, 'godot.log');
  const inner = [
    'SLACK_PERF=1',
    objects ? 'SLACK_PERF_OBJECTS=1' : '',
    'DISPLAY=' + (process.env.DISPLAY ?? ':1'),
    'SLACK_USER_ROOT=' + JSON.stringify(path.join(runRoot, 'user')),
    JSON.stringify(godot),
    '--path', JSON.stringify(project),
    '--audio-driver Dummy',
    '--log-file', JSON.stringify(logFile),
    fullscreen ? '--fullscreen' : '',
    'res://Main.tscn',
    '-- --audit --room ' + entryRoom + ' --frames 300 --dialog-choice 1 --live-frames ' + frames
  ].filter(part => part.length > 0).join(' ');
  let text = '';
  let abort = null;
  const code = await new Promise((resolve, reject) => {
    const child = spawn('node', [path.join(project, 'Tools', 'agent-lock.mjs'), 'godot', inner], { cwd: project, env: process.env, stdio: ['ignore', 'pipe', 'pipe'] });
    const limit = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('frame-profile capture timed out after ' + Math.round(seconds + 240) + 's')); }, (seconds + 240) * 1000);
    const watch = setInterval(async () => {
      if (abort !== null) return;
      const probe = await firstCadenceRoom(runRoot);
      if (probe.room === null || probe.room === expectedRoom) return;
      abort = 'entered room ' + probe.room + ' but expected room ' + expectedRoom + ' (first cadence record in ' + probe.log + '); aborting before measuring';
      clearInterval(watch);
      child.kill('SIGTERM');
    }, 1000);
    child.stdout.on('data', chunk => { text += chunk; });
    child.stderr.on('data', chunk => { text += chunk; });
    child.on('error', reject);
    child.on('exit', status => { clearTimeout(limit); clearInterval(watch); resolve(status ?? 1); });
  });
  await writeFile(path.join(runRoot, 'process.log'), text);
  if (abort !== null) throw new Error(abort);
  if (code !== 0) throw new Error('godot exited ' + code + '; see ' + path.join(runRoot, 'process.log'));
  const live = /LIVE_PERF .*/.exec(text);
  return { logFile: await newestPerformanceLog(runRoot), liveLine: live ? live[0] : null, frames, command: inner };
}

function selectRoom(cadence) {
  const counts = new Map();
  for (const record of cadence) counts.set(record.room, (counts.get(record.room) ?? 0) + 1);
  const rooms = [...counts.entries()].map(([room, windows]) => ({ room, windows })).sort((a, b) => b.windows - a.windows);
  const dominant = rooms[0];
  let chosen = dominant;
  if (requestedRoom !== null) {
    const match = rooms.find(entry => entry.room === requestedRoom);
    if (!match) throw new Error('room ' + requestedRoom + ' has no cadence records; present rooms: ' + rooms.map(entry => entry.room + '(' + entry.windows + ')').join(', '));
    chosen = match;
  }
  const records = cadence.filter(record => record.room === chosen.room);
  return {
    room: chosen.room,
    windows: chosen.windows,
    rooms,
    dominantRoom: dominant.room,
    discardedWindows: cadence.length - chosen.windows,
    discardedRooms: rooms.filter(entry => entry.room !== chosen.room),
    records
  };
}

async function analyze(source) {
  const raw = await readFile(source, 'utf8');
  const records = raw.split('\n').filter(line => line.trim().length > 0).map(line => JSON.parse(line));
  const cadence = records.filter(record => record.type === 'cadence');
  if (cadence.length === 0) throw new Error('no cadence records in ' + source);
  const selection = selectRoom(cadence);
  if (replay === null && selection.dominantRoom !== expectedRoom) {
    throw new Error('capture expected room ' + expectedRoom + ' to dominate but room ' + selection.dominantRoom + ' did; windows=' + selection.rooms.map(entry => entry.room + ':' + entry.windows).join(' ') + ' (entry room ' + entryRoom + ')');
  }
  const scoped = selection.records;
  const first = scoped[0];
  const elapsed = scoped.map(record => (Date.parse(record.time) - Date.parse(first.time)) / 1000);
  const steady = scoped.filter((record, index) => elapsed[index] > warmup);
  if (steady.length === 0) throw new Error('no steady-state cadence windows for room ' + selection.room + ' after a ' + warmup + 's warmup');
  const steadySeconds = (Date.parse(steady[steady.length - 1].time) - Date.parse(steady[0].time)) / 1000 + (steady[0].seconds ?? 0);
  const allRows = steady.map((record, index) => ({
    record,
    index,
    frame: record.frame,
    time: record.time,
    seconds: elapsed[index + (scoped.length - steady.length)],
    processMeanMs: record.process?.meanMs ?? null,
    processP50Ms: record.process?.p50Ms ?? null,
    processP99Ms: record.process?.p99Ms ?? null,
    renderMeanMs: record.renderInterval?.meanMs ?? null,
    renderP95Ms: record.renderInterval?.p95Ms ?? null,
    renderMaxMs: record.renderInterval?.maxMs ?? null,
    buildMeanMs: record.build?.meanMs ?? null,
    buildMaxMs: record.build?.maxMs ?? null,
    secondsInWindow: record.seconds ?? null,
    cpuMs: record.cpuMs ?? null,
    mainThreadCpuMs: record.mainThreadCpuMs ?? null,
    aliveInstances: record.alive && typeof record.alive.instances === 'number' ? record.alive.instances : null,
    perLogicStepMs: (record.process && typeof record.process.meanMs === 'number' && record.process.count > 0 && record.logicSteps > 0)
      ? record.process.meanMs * record.process.count / record.logicSteps
      : null,
    completedRenderFps: record.completedRenderFps ?? null,
    producedFrames: rendersOnScreen(record)
  }));
  const invalidRows = allRows.filter(row => !row.producedFrames);
  const validRatio = allRows.length === 0 ? 0 : (allRows.length - invalidRows.length) / allRows.length;
  const validWindows = allRows.length - invalidRows.length;
  const windowValid = validRatio >= minValidRatio;
  if (!windowValid && !allowInvalid) {
    throw new Error('measurement invalid: only ' + validWindows + '/' + allRows.length + ' steady windows produced frames (' + Math.round(validRatio * 100) + '% < ' + Math.round(minValidRatio * 100) + '% required); run fullscreen (default) instead of --windowed, or pass --allow-invalid to inspect anyway');
  }
  const rows = allRows.filter(row => row.producedFrames);
  if (rows.length === 0) throw new Error('measurement invalid: 0 steady windows produced frames; run fullscreen (default) instead of --windowed');
  const renderSplit = splitOutliers(rows, row => row.renderMaxMs);
  const renderKept = renderSplit.kept;
  const renderP95Kept = nonNull(renderKept.map(row => row.renderP95Ms));
  const renderP95All = nonNull(rows.map(row => row.renderP95Ms));
  const renderAllTimes = nonNull(rows.map(row => row.renderMaxMs));
  const renderStats = describe(nonNull(rows.map(row => row.renderMeanMs)));
  const renderKeptStats = describe(nonNull(renderKept.map(row => row.renderMeanMs)));
  const aliveSeries = rows.map(entry => aliveInstances(entry.record));
  const aliveValues = nonNull(aliveSeries.map(entry => entry.value));
  const aliveSource = aliveSeries.find(entry => entry.source !== 'absent')?.source ?? 'absent';
  const particleValues = nonNull(rows.map(row => row.record?.alive?.particles ?? null));
  const plan = rows.map(row => row.record.framePlan).filter(Boolean);
  const sizes = rows.map(row => row.record.renderSize).filter(Boolean);
  const size = sizes.length > 0 ? sizes[sizes.length - 1] : null;
  const attributedPixels = size !== null && typeof size.windowPixels === 'number' && size.windowPixels > 0 ? size.windowPixels : null;
  const framebufferPixelsWarning = 'semantically unclear; GetTexture().GetWidth()/GetHeight() disagrees with GetImage() output in the same process and tick; do not use for pixel attribution';
  const processStats = describe(nonNull(rows.map(row => row.processMeanMs)));
  const roomSpeed = first.targetLogicFps ?? 0;
  const cpuStats = describe(nonNull(rows.map(row => row.cpuMs)));
  const mainThreadStats = describe(nonNull(rows.map(row => row.mainThreadCpuMs)));
  const growth = aliveValues.length >= minSamples ? regression(aliveValues) : null;
  const correlation = aliveValues.length >= minSamples && renderP95All.length >= minSamples ? pearson(aliveValues, renderP95All) : null;
  const correlationReports = {
    perStepVsFullThreadCpu: correlationReport(rows, allRows, 'perStep vs 全线程CPU核数',
      row => row.perLogicStepMs,
      row => row.cpuMs === null || row.secondsInWindow === null ? null : row.cpuMs / (row.secondsInWindow * 1000)),
    renderIntervalVsFullThreadCpu: correlationReport(rows, allRows, 'renderInterval vs 全线程CPU核数',
      row => row.renderMeanMs,
      row => row.cpuMs === null || row.secondsInWindow === null ? null : row.cpuMs / (row.secondsInWindow * 1000)),
    renderIntervalVsPerStep: correlationReport(rows, allRows, 'renderInterval vs perStep',
      row => row.renderMeanMs,
      row => row.perLogicStepMs),
    renderIntervalVsAlive: correlationReport(rows, allRows, 'renderInterval vs alive.instances',
      row => row.renderMeanMs,
      row => row.aliveInstances),
    aliveVsRenderP95: correlationReport(rows, allRows, 'alive.instances vs renderInterval.p95',
      row => row.aliveInstances,
      row => row.renderP95Ms)
  };
  const renderBudgetDecidable = renderP95Kept.length > 0;
  const renderP95Steady = percentile(renderP95Kept, 0.5);
  const renderSpike = renderAllTimes.length > 0 ? Math.max(...renderAllTimes) : null;
  const unbounded = growth !== null && growth.slope > 0 && growth.r2 > 0.9;
  const slowRender = renderBudgetDecidable && renderP95Steady > thresholdMs;
  const invariantReport = checkBuckets(rows);
  if (!invariantReport.ok) {
    const first = invariantReport.violations[0];
    throw new Error('三桶不变量被破坏：' + first.field + ' 的 ' + first.probe + ' 计数 ' + first.actual + ' != ' + first.label + ' ' + first.expected + '（frame ' + first.frame + '）——探针放错层，本次测量无效');
  }
  const unanswered = [];
  if (aliveValues.length < minSamples) unanswered.push('alive 是否无界增长（样本 n=' + aliveValues.length + ' < ' + minSamples + '）');
  if (correlation === null) unanswered.push('alive 与 renderInterval.p95 的相关性（样本 n=' + aliveValues.length + '）');
  if (!renderBudgetDecidable) unanswered.push('呈现间隔是否超过 ' + thresholdMs + 'ms');
  if (buildMeansMissing(rows)) unanswered.push('build 耗时（日志无 build 字段）');
  if (size === null) unanswered.push('renderSize 像素规模（日志无 renderSize 字段）');
  if (plan.length === 0) unanswered.push('framePlan 增量（日志无 framePlan 字段）');
  if (cpuStats === null) unanswered.push('cpuMs 与 process.meanMs 的对比（日志无 cpuMs 字段）');
  const result = {
    type: 'frame-profile',
    label,
    generatedAt: new Date().toISOString(),
    source,
    room: selection.room,
    entryRoom: replay === null ? entryRoom : null,
    rooms: selection.rooms,
    dominance: { dominantRoom: selection.dominantRoom, selectedRoom: selection.room },
    roomSpeed: first.targetLogicFps,
    cadenceWindows: cadence.length,
    roomWindows: selection.windows,
    discardedWindows: selection.discardedWindows,
    discardedRooms: selection.discardedRooms,
    steadyWindows: steady.length,
    validity: {
      mode: replay === null ? (fullscreen ? 'fullscreen' : 'windowed') : 'replay',
      steadyWindows: allRows.length,
      producedFrames: validWindows,
      droppedNoFrames: invalidRows.length,
      validRatio,
      minValidRatio,
      windowValid,
      droppedWindows: invalidRows.map(row => ({ time: row.time, frame: row.frame, room: row.record.room, seconds: Math.round(row.seconds * 1000) / 1000, completedRenderFps: row.completedRenderFps, renderIntervalCount: row.record.renderInterval?.count ?? 0 }))
    },
    warmupDiscardedSeconds: warmup,
    steadySeconds,
    meetsDuration: steadySeconds >= seconds,
    requiredSeconds: seconds,
    minSamples,
    fields: {
      p50Ms: nonNull(rows.map(row => row.processP50Ms)).length > 0,
      p99Ms: nonNull(rows.map(row => row.processP99Ms)).length > 0,
      build: nonNull(rows.map(row => row.buildMeanMs)).length > 0,
      renderSize: size !== null,
      framePlan: plan.length > 0,
      alive: aliveSource === 'alive.instances',
      cpuMs: cpuStats !== null,
      aliveSource
    },
    unanswered,
    process: { windowMean: processStats },
    renderInterval: {
      windowMean: renderStats,
      windowP95MsMedian: percentile(renderP95All, 0.5),
      windowP95MsWorst: renderP95All.length > 0 ? Math.max(...renderP95All) : null,
      steadyP95Ms: renderBudgetDecidable ? renderP95Steady : null,
      spikeMaxMs: renderSpike,
      fence: renderSplit.limits,
      outlierMode: 'renderInterval.maxMs > Q3 + ' + outlierMultiplier + '*IQR',
      outlierWindows: renderSplit.outliers.length,
      keptWindows: renderKept.length,
      excludedWindowMean: renderKeptStats,
      excludedP95MsMedian: renderP95Kept.length > 0 ? percentile(renderP95Kept, 0.5) : null,
      excludedP95MsWorst: renderP95Kept.length > 0 ? Math.max(...renderP95Kept) : null,
      outliers: renderSplit.outliers.map(row => ({ time: row.time, frame: row.frame, room: row.record.room, seconds: Math.round(row.seconds * 1000) / 1000, maxMs: row.renderMaxMs, meanMs: row.renderMeanMs, p95Ms: row.renderP95Ms }))
    },
    build: describe(nonNull(rows.map(row => row.buildMeanMs))) === null ? null : {
      meanMs: mean(nonNull(rows.map(row => row.buildMeanMs))),
      maxMs: nonNull(rows.map(row => row.buildMaxMs)).length > 0 ? Math.max(...nonNull(rows.map(row => row.buildMaxMs))) : null,
      buildsPerWindow: mean(nonNull(rows.map(row => row.record.build?.count ?? null)))
    },
    phases: (() => {
      const withPhases = rows.filter(row => row.record.phases);
      if (withPhases.length === 0) return null;
      const names = [...new Set(withPhases.flatMap(row => Object.keys(row.record.phases)))];
      const perStep = {};
      for (const name of names) {
        const values = withPhases.map(row => row.record.phases[name]).filter(Boolean).map(entry => entry.meanMs);
        if (values.length === 0) continue;
        perStep[name] = { meanMsPerStep: describe(values), samplesPerWindow: percentile(withPhases.map(row => row.record.phases[name]).filter(Boolean).map(entry => entry.count), 0.5) };
      }
      const instances = withPhases.map(row => row.record.alive?.instances ?? null).filter(value => typeof value === 'number' && value > 0);
      const instanceStats = instances.length === 0 ? null : { min: Math.min(...instances), max: Math.max(...instances), spread: Math.max(...instances) - Math.min(...instances) };
      const perInstance = {};
      for (const name of ['begin_input', 'animation_room', 'motion_boundary', 'step', 'collision_end']) {
        const values = [];
        for (const row of withPhases) {
          const entry = row.record.phases[name];
          const alive = row.record.alive?.instances;
          if (!entry || typeof alive !== 'number' || alive <= 0) continue;
          values.push(entry.meanMs / alive * 1000);
        }
        if (values.length > 0) perInstance[name] = { usPerInstancePerStep: describe(values) };
      }
      const totalPerStep = perStep.total?.meanMsPerStep?.p50Ms ?? null;
      const linearCheck = totalPerStep === null || instanceStats === null ? null : {
        decidable: instanceStats.spread / Math.max(1, instanceStats.min) >= 0.05,
        spreadPercent: Math.round(instanceStats.spread / Math.max(1, instanceStats.min) * 1000) / 10,
        verdict: instanceStats.spread / Math.max(1, instanceStats.min) >= 0.05
          ? '实例数有足够方差，可检验线性关系'
          : '实例数方差仅 ' + (Math.round(instanceStats.spread / Math.max(1, instanceStats.min) * 10000) / 100) + '%，无法检验线性关系；需要不同实例数的对照房间'
      };
      return {
        sampleBasis: 'phases.count 按逻辑步计数（不是 _Process 次数）；meanMsPerStep 是每逻辑步耗时',
        windows: withPhases.length,
        perStep,
        perInstancePerStep: perInstance,
        perInstanceNote: 'usPerInstancePerStep 仅在同一实例数区间内可比；跨实例数的比较需要线性关系成立',
        instanceStats,
        linearCheck
      };
    })(),
    buckets: (() => {
      const bucket = (field, denominator) => {
        const windows = rows.filter(row => row.record[field]);
        if (windows.length === 0) return null;
        const names = [...new Set(windows.flatMap(row => Object.keys(row.record[field])))];
        const entries = {};
        for (const name of names) {
          const present = windows.map(row => row.record[field][name]).filter(Boolean);
          entries[name] = {
            meanMsPerCall: describe(present.map(entry => entry.meanMs)),
            meanMsPerSecond: describe(present.map(entry => entry.meanMs * entry.count))
          };
        }
        return { field, denominator, windows: windows.length, perCall: entries };
      };
      const process = bucket('processPhases', 'process.count');
      const draw = bucket('drawPhases', 'present.count');
      const step = (() => {
        const windows = rows.filter(row => row.record.phases);
        if (windows.length === 0) return null;
        const names = [...new Set(windows.flatMap(row => Object.keys(row.record.phases)))];
        const entries = {};
        for (const name of names) {
          const present = windows.map(row => row.record.phases[name]).filter(Boolean);
          entries[name] = {
            meanMsPerStep: describe(present.map(entry => entry.meanMs)),
            meanMsPerSecond: describe(present.map(entry => entry.meanMs * entry.count)),
            logicStepsPerWindow: describe(windows.map(row => row.record.logicSteps).filter(value => typeof value === 'number'))
          };
        }
        return { field: 'phases', denominator: 'logicSteps', windows: windows.length, perCall: entries };
      })();
      return { process, draw, step };
    })(),
    invariants: checkBuckets(rows),
    processBreakdown: (() => {
      const pick = (field, sub) => nonNull(rows.map(row => row.record.processBreakdown?.[field]?.[sub] ?? null));
      const segment = (field, sub) => describe(pick(field, sub));
      const period = nonNull(rows.map(row => row.record.processFps > 0 ? 1000 / row.record.processFps : null));
      const processWall = pick('processBreakdown', 'processWall');
      const drawLatency = pick('processBreakdown', 'drawLatency');
      const idleWall = pick('processBreakdown', 'idleWall');
      const paired = rows.map(row => ({
        period: row.record.processFps > 0 ? 1000 / row.record.processFps : null,
        processWall: row.record.processBreakdown?.processWall?.meanMs ?? null,
        drawLatency: row.record.processBreakdown?.drawLatency?.meanMs ?? null,
        idleWall: row.record.processBreakdown?.idleWall?.meanMs ?? null
      })).filter(entry => entry.period !== null && entry.processWall !== null && entry.drawLatency !== null && entry.idleWall !== null);
      const errors = paired.map(entry => (entry.processWall + entry.drawLatency + entry.idleWall - entry.period) / entry.period * 100);
      const bySteps = {};
      for (const row of rows) {
        const steps = row.record.processBreakdown?.bySteps;
        if (!steps) continue;
        for (const [name, entry] of Object.entries(steps)) (bySteps[name] ??= []).push(entry.meanMs);
      }
      return {
        unlocked: paired.length > 0,
        processWall: segment('processBreakdown', 'processWall'),
        drawLatency: segment('processBreakdown', 'drawLatency'),
        idleWall: segment('processBreakdown', 'idleWall'),
        period: describe(period),
        closure: errors.length === 0 ? null : { medianPercent: percentile(errors, 0.5), worstPercent: Math.max(...errors.map(Math.abs)), windows: errors.length },
        closureNote: '闭合恒等式：processWall + drawLatency + idleWall == 1000/processFps；恒等式本身零信息量，只用它的残差判断探针是否漏桶',
        bySteps: Object.fromEntries(Object.entries(bySteps).map(([name, values]) => [name, { meanMs: mean(values), p50Ms: percentile(values, 0.5), windows: values.length }]))
      };
    })(),
    pixelAttribution: {
      field: 'renderSize.windowPixels',
      pixels: attributedPixels,
      note: '像素归因一律使用 windowPixels；framebufferPixels 语义未定，已排除'
    },
    renderSize: size === null ? null : {
      framebufferPixelsWarning,
      width: size.width,
      height: size.height,
      pixels: size.pixels,
      canvasWidth: size.canvasWidth ?? null,
      canvasHeight: size.canvasHeight ?? null,
      canvasPixels: size.canvasPixels ?? null,
      windowWidth: size.windowWidth ?? null,
      windowHeight: size.windowHeight ?? null,
      windowPixels: size.windowPixels ?? null,
      framebufferWidth: size.framebufferWidth ?? null,
      framebufferHeight: size.framebufferHeight ?? null,
      framebufferPixels: size.framebufferPixels ?? null,
      ratioToRenderPixels: size.pixels / (640 * 480),
      windowRatioTo640x480: (size.windowPixels ?? 0) / (640 * 480),
      framebufferRatioTo640x480: null
    },
    framePlan: plan.length === 0 ? null : {
      published: plan.reduce((sum, entry) => sum + (entry.published ?? 0), 0),
      rendered: plan.reduce((sum, entry) => sum + (entry.rendered ?? 0), 0),
      coalesced: plan.reduce((sum, entry) => sum + (entry.coalesced ?? 0), 0),
      skipped: plan.reduce((sum, entry) => sum + (entry.skipped ?? 0), 0)
    },
    alive: {
      source: aliveSource,
      samples: aliveValues.length,
      series: rows.map((row, index) => ({ frame: row.frame, seconds: Math.round(row.seconds * 1000) / 1000, instances: aliveSeries[index].value, particles: row.record.alive?.particles ?? null })),
      instancesStart: aliveValues.length > 0 ? aliveValues[0] : null,
      instancesEnd: aliveValues.length > 0 ? aliveValues[aliveValues.length - 1] : null,
      instancesMin: aliveValues.length > 0 ? Math.min(...aliveValues) : null,
      instancesMax: aliveValues.length > 0 ? Math.max(...aliveValues) : null,
      particlesStart: particleValues.length > 0 ? particleValues[0] : null,
      particlesEnd: particleValues.length > 0 ? particleValues[particleValues.length - 1] : null
    },
    cpu: cpuStats === null ? null : {
      meanMs: cpuStats.meanMs,
      maxMs: cpuStats.maxMs,
      coresUsed: mean(nonNull(rows.map(row => row.cpuMs === null || row.secondsInWindow === null ? null : row.cpuMs / (row.secondsInWindow * 1000))))
    },
    mainThreadCpu: mainThreadStats === null ? null : {
      meanMs: mainThreadStats.meanMs,
      maxMs: mainThreadStats.maxMs,
      coresUsed: mean(nonNull(rows.map(row => row.mainThreadCpuMs === null || row.secondsInWindow === null ? null : row.mainThreadCpuMs / (row.secondsInWindow * 1000))))
    },
    correlations: {
      note: '每条相关系数都附 n、样本范围、warmup 处理与敏感性检验；符号不稳定时不得作为结论',
      perStepVsFullThreadCpu: correlationReports.perStepVsFullThreadCpu,
      renderIntervalVsFullThreadCpu: correlationReports.renderIntervalVsFullThreadCpu,
      renderIntervalVsPerStep: correlationReports.renderIntervalVsPerStep,
      renderIntervalVsAlive: correlationReports.renderIntervalVsAlive,
      aliveVsRenderP95: correlationReports.aliveVsRenderP95
    },
    perLogicStep: (() => {
      const values = [];
      let processTotal = 0;
      let stepsTotal = 0;
      for (const row of rows) {
        const total = row.processMeanMs === null ? null : row.processMeanMs * (row.record.process?.count ?? 0);
        if (total === null || !row.record.logicSteps) continue;
        values.push(total / row.record.logicSteps);
        processTotal += total;
        stepsTotal += row.record.logicSteps;
      }
      if (values.length === 0) return null;
      const stats = describe(values);
      const pooled = stepsTotal === 0 ? null : processTotal / stepsTotal;
      const budgetMs = roomSpeed > 0 ? 1000 / roomSpeed : null;
      const wallClockMs = stepsTotal === 0 ? null : rows.reduce((sum, row) => sum + (row.secondsInWindow ?? 0) * 1000, 0) / stepsTotal;
      return {
        ...stats,
        pooledMs: pooled,
        budgetMs,
        budgetSource: 'room_speed=' + roomSpeed + ' => 1000/' + roomSpeed,
        percentOfBudget: budgetMs === null || pooled === null ? null : pooled / budgetMs * 100,
        wallClockPerStepMs: wallClockMs,
        note: 'perLogicStep = process.meanMs * process.count / logicSteps；budgetMs 用 room_speed 推导，不是 60fps 的 16.67ms'
      };
    })(),
    perRenderedFrame: (() => {
      const values = [];
      for (const row of rows) {
        const count = row.record.renderInterval?.count ?? 0;
        if (count === 0 || row.renderMeanMs === null) continue;
        const processTotal = (row.processMeanMs ?? 0) * (row.record.process?.count ?? 0);
        values.push({ interval: row.renderMeanMs, process: processTotal / count });
      }
      if (values.length === 0) return null;
      const interval = mean(values.map(entry => entry.interval));
      const process = mean(values.map(entry => entry.process));
      const latency = nonNull(rows.map(row => row.record.processBreakdown?.drawLatency?.meanMs ?? null));
      const idle = nonNull(rows.map(row => row.record.processBreakdown?.idleWall?.meanMs ?? null));
      return {
        intervalMs: interval,
        processMs: process,
        drawLatencyMs: latency.length === 0 ? null : percentile(latency, 0.5),
        idleWallMs: idle.length === 0 ? null : percentile(idle, 0.5),
        remainderMs: interval - process,
        remainderIsNotDrawLatency: '其余 = 间隔 - _Process；它包含 drawLatency 与 idleWall，但二者不等价于该差值，逐帧上屏延迟必须直接读 processBreakdown.drawLatency'
      };
    })(),
    renderedFps: mean(nonNull(rows.map(row => row.completedRenderFps))),
    logicFps: mean(nonNull(rows.map(row => row.record.logicFps ?? null))),
    catchUp: {
      processesPerWindow: mean(nonNull(rows.map(row => row.record.process?.count ?? null))),
      catchUpPerWindow: mean(nonNull(rows.map(row => row.record.catchUpProcesses ?? null))),
      maxStepsPerProcess: mean(nonNull(rows.map(row => row.record.maxStepsPerProcess ?? null))),
      logicStepsPerWindow: mean(nonNull(rows.map(row => row.record.logicSteps ?? null)))
    },
    criteria: {
      aliveGrowth: growth === null ? {
        decidable: false,
        verdict: '无法判定：alive 样本不足（n=' + aliveValues.length + '，需要 ≥' + minSamples + '）'
      } : {
        decidable: true,
        slope: growth.slope,
        r2: growth.r2,
        unbounded,
        verdict: unbounded
          ? '疑似无界增长：alive.instances 斜率 ' + growth.slope.toFixed(3) + '/窗口，R²=' + growth.r2.toFixed(3) + '（斜率>0 且 R²>0.9）'
          : growth.slope > 0
            ? '未观察到无界增长：斜率 ' + growth.slope.toFixed(3) + '/窗口但 R²=' + growth.r2.toFixed(3) + ' ≤0.9，趋势不成立'
            : '无增长：斜率 ' + growth.slope.toFixed(3) + '/窗口（≤0），R²=' + growth.r2.toFixed(3)
      },
      correlation: (() => {
        const entry = correlation === null ? null : correlationReports?.aliveVsRenderP95 ?? null;
        if (entry === null || entry.r === null) return {
          decidable: false,
          verdict: '无法判定：可用配对样本不足（alive n=' + aliveValues.length + '，renderP95 n=' + renderP95All.length + '，需要 ≥' + minSamples + '）'
        };
        const stable = entry.sensitivity.signStable !== false;
        const magnitude = Math.abs(entry.r) >= 0.7 ? '强' : Math.abs(entry.r) >= 0.5 ? '中等' : Math.abs(entry.r) >= 0.3 ? '弱' : '极弱';
        return {
          decidable: stable,
          r: entry.r,
          count: entry.n,
          strong: Math.abs(entry.r) >= 0.5,
          signStable: entry.sensitivity.signStable,
          sensitivityRange: entry.sensitivity.spread,
          verdict: stable
            ? 'alive.instances 与 renderInterval.p95 的皮尔逊 r=' + entry.r.toFixed(3) + '（n=' + entry.n + '，敏感性区间 [' + entry.sensitivity.spread.min.toFixed(3) + ',' + entry.sensitivity.spread.max.toFixed(3) + ']，符号稳定）→ ' + magnitude + '相关'
            : '无法判定：r=' + entry.r.toFixed(3) + '（n=' + entry.n + '）但敏感性区间 [' + entry.sensitivity.spread.min.toFixed(3) + ',' + entry.sensitivity.spread.max.toFixed(3) + '] 跨零，符号随口径翻转，不足以判定相关性'
        };
      })(),
      renderBudget: renderBudgetDecidable ? {
        decidable: true,
        steadyP95Ms: renderP95Steady,
        spikeMaxMs: renderSpike,
        thresholdMs,
        slowRender,
        renderedFps: mean(nonNull(rows.map(row => row.completedRenderFps))),
        renderFpsCaveat: 'completedRenderFps 是 FramePostDraw 回调率（绘制提交率），不严格等同像素上屏率；在本机 llvmpipe 下二者可能接近但未经独立验证',
        verdict: '呈现间隔 p95=' + renderP95Steady.toFixed(2) + 'ms → 约 ' + (renderP95Steady === 0 ? 'n/a' : (1000 / renderP95Steady).toFixed(2)) + ' Hz；尖峰 max=' + (renderSpike === null ? 'n/a' : renderSpike.toFixed(2) + 'ms') + '。' + (slowRender ? '低于 ' + targetPresentHz + 'Hz 目标（' + targetPresentHz + 'Hz 预算为 ' + thresholdMs.toFixed(2) + 'ms），差额 ' + (thresholdMs === 0 ? 'n/a' : (renderP95Steady / thresholdMs).toFixed(1)) + ' 倍' : '未超 ' + targetPresentHz + 'Hz 预算 ' + thresholdMs.toFixed(2) + 'ms')
      } : {
        decidable: false,
        verdict: '无法判定：无 renderInterval 稳态样本'
      }
    }
  };
  return result;
}

function buildMeansMissing(rows) {
  return nonNull(rows.map(row => row.buildMeanMs)).length === 0;
}

function summarize(result) {
  const round = value => typeof value === 'number' ? Math.round(value * 100) / 100 : value;
  const lines = [];
  lines.push('FRAME_PROFILE label=' + result.label + ' room=' + result.room + ' (dominant=' + result.dominance.dominantRoom + ') roomSpeed=' + result.roomSpeed + ' windows=' + result.roomWindows + (result.entryRoom === null ? '' : ' entryRoom=' + result.entryRoom));
  lines.push('FRAME_PROFILE rooms=' + result.rooms.map(entry => entry.room + ':' + entry.windows).join(' ') + ' discarded_other_rooms=' + result.discardedWindows + (result.discardedRooms.length > 0 ? ' (' + result.discardedRooms.map(entry => entry.room + ':' + entry.windows).join(' ') + ')' : ''));
  const v = result.validity;
  lines.push('FRAME_PROFILE mode=' + v.mode + ' steady_windows=' + v.steadyWindows + ' produced_frames=' + v.producedFrames + ' dropped_no_frames=' + v.droppedNoFrames + ' valid_ratio=' + round(v.validRatio * 100) + '% (需要 ≥' + round(v.minValidRatio * 100) + '%) → ' + (v.windowValid ? '有效' : '无效'));
  for (const dropped of v.droppedWindows.slice(0, 10)) lines.push('FRAME_PROFILE 丢弃窗口(无帧上屏) time=' + dropped.time + ' frame=' + dropped.frame + ' room=' + dropped.room + ' t=' + dropped.seconds + 's completedRenderFps=' + round(dropped.completedRenderFps) + ' renderInterval.count=' + dropped.renderIntervalCount);
  if (v.droppedWindows.length > 10) lines.push('FRAME_PROFILE 丢弃窗口 其余 ' + (v.droppedWindows.length - 10) + ' 条见 json');
  lines.push('FRAME_PROFILE duration steady=' + round(result.steadySeconds) + 's windows=' + result.steadyWindows + ' warmup_dropped=' + result.warmupDiscardedSeconds + 's meets_requirement=' + result.meetsDuration + (result.meetsDuration ? '' : ' (需要 ' + result.requiredSeconds + 's)'));
  const process = result.process.windowMean;
  if (process) lines.push('FRAME_PROFILE process(1s窗口meanMs) n=' + process.count + ' p50=' + round(process.p50Ms) + ' p95=' + round(process.p95Ms) + ' p99=' + round(process.p99Ms) + ' max=' + round(process.maxMs) + ' mean=' + round(process.meanMs));
  else lines.push('FRAME_PROFILE process 无样本');
  const render = result.renderInterval;
  if (render.windowMean) lines.push('FRAME_PROFILE renderInterval(全部窗口) n=' + render.windowMean.count + ' p50=' + round(render.windowMean.p50Ms) + ' p95=' + round(render.windowMean.p95Ms) + ' p99=' + round(render.windowMean.p99Ms) + ' max=' + round(render.windowMean.maxMs) + ' mean=' + round(render.windowMean.meanMs));
  if (render.outlierWindows > 0 && render.excludedWindowMean) lines.push('FRAME_PROFILE renderInterval(剔除' + render.outlierWindows + '个离群窗口) n=' + render.excludedWindowMean.count + ' p50=' + round(render.excludedWindowMean.p50Ms) + ' p95=' + round(render.excludedWindowMean.p95Ms) + ' p99=' + round(render.excludedWindowMean.p99Ms) + ' max=' + round(render.excludedWindowMean.maxMs) + ' mean=' + round(render.excludedWindowMean.meanMs));
  lines.push('FRAME_PROFILE renderInterval.p95(每窗口) 全部median=' + round(render.windowP95MsMedian) + ' 全部worst=' + round(render.windowP95MsWorst) + ' 剔除后median=' + round(render.excludedP95MsMedian) + ' 剔除后worst=' + round(render.excludedP95MsWorst));
  if (render.spikeMaxMs !== null) lines.push('FRAME_PROFILE renderInterval 最大单窗口尖峰=' + round(render.spikeMaxMs) + 'ms（参照：1000/' + targetPresentHz + '=' + round(1000 / targetPresentHz) + 'ms 为 ' + targetPresentHz + 'Hz 呈现预算；room_speed 的逻辑预算是 ' + round(1000 / (result.roomSpeed || 1)) + 'ms，两者不同维度）');
  if (render.fence) lines.push('FRAME_PROFILE 离群判定 renderInterval.maxMs > Q3+' + outlierMultiplier + '*IQR = ' + round(render.fence.high) + 'ms (Q1=' + round(render.fence.q1) + ' Q3=' + round(render.fence.q3) + ' IQR=' + round(render.fence.iqr) + ')');
  for (const outlier of render.outliers) lines.push('FRAME_PROFILE 离群窗口 time=' + outlier.time + ' frame=' + outlier.frame + ' room=' + outlier.room + ' t=' + outlier.seconds + 's renderInterval.max=' + round(outlier.maxMs) + 'ms mean=' + round(outlier.meanMs) + ' p95=' + round(outlier.p95Ms));
  if (result.build) lines.push('FRAME_PROFILE build mean=' + round(result.build.meanMs) + 'ms 单次最坏=' + (result.build.maxMs === null ? 'n/a（C# 未发 build.maxMs）' : round(result.build.maxMs) + 'ms') + ' 每窗口次数=' + round(result.build.buildsPerWindow));
  else lines.push('FRAME_PROFILE build 缺失（需要 task-5 的 build 字段）');
  if (result.renderSize) {
    const size = result.renderSize;
    lines.push('FRAME_PROFILE renderSize 渲染=' + size.width + 'x' + size.height + ' (' + size.pixels + 'px) 相对640x480=' + round(size.ratioToRenderPixels) + 'x');
    if (size.framebufferPixels !== null && size.framebufferPixels > 0) lines.push('FRAME_PROFILE renderSize framebuffer=' + size.framebufferWidth + 'x' + size.framebufferHeight + ' (' + size.framebufferPixels + 'px) 相对640x480=' + round(size.framebufferRatioTo640x480) + 'x');
    if (size.canvasPixels !== null && size.canvasPixels > 0) lines.push('FRAME_PROFILE renderSize canvas=' + size.canvasWidth + 'x' + size.canvasHeight + ' (' + size.canvasPixels + 'px)');
  }
  else lines.push('FRAME_PROFILE renderSize 缺失（需要 task-5 的 renderSize 字段）');
  if (result.framePlan) lines.push('FRAME_PROFILE framePlan published=' + result.framePlan.published + ' rendered=' + result.framePlan.rendered + ' coalesced=' + result.framePlan.coalesced + ' skipped=' + result.framePlan.skipped);
  else lines.push('FRAME_PROFILE framePlan 缺失（需要 task-5 的 framePlan 字段）');
  lines.push('FRAME_PROFILE alive source=' + result.alive.source + ' n=' + result.alive.samples + ' start=' + result.alive.instancesStart + ' end=' + result.alive.instancesEnd + ' min=' + result.alive.instancesMin + ' max=' + result.alive.instancesMax);
  if (result.alive.particlesStart !== null) lines.push('FRAME_PROFILE alive.particles start=' + result.alive.particlesStart + ' end=' + result.alive.particlesEnd);
  if (result.cpu) lines.push('FRAME_PROFILE cpu(全线程) ' + round(result.cpu.coresUsed) + ' 核');
  else lines.push('FRAME_PROFILE cpuMs 缺失');
  if (result.mainThreadCpu) lines.push('FRAME_PROFILE cpu(主线程) ' + round(result.mainThreadCpu.coresUsed) + ' 核  占全线程 ' + round(result.cpu === null || result.cpu.coresUsed === 0 ? 0 : result.mainThreadCpu.coresUsed / result.cpu.coresUsed * 100) + '%');
  else lines.push('FRAME_PROFILE mainThreadCpuMs 缺失');
  if (result.logicFps !== null) lines.push('FRAME_PROFILE 速率 logicFps=' + round(result.logicFps) + ' renderedFps=' + round(result.renderedFps) + ' 每上屏帧推进逻辑步=' + round(result.logicFps === 0 ? 0 : result.logicFps / result.renderedFps));
  if (result.catchUp) lines.push('FRAME_PROFILE 追赶 _Process/窗口=' + round(result.catchUp.processesPerWindow) + ' 其中catchUp=' + round(result.catchUp.catchUpPerWindow) + ' maxSteps=' + round(result.catchUp.maxStepsPerProcess) + ' logicSteps/窗口=' + round(result.catchUp.logicStepsPerWindow));
  if (result.perLogicStep) {
    const step = result.perLogicStep;
    lines.push('FRAME_PROFILE perLogicStepMs p50=' + round(step.p50Ms) + ' p95=' + round(step.p95Ms) + ' p99=' + round(step.p99Ms) + ' max=' + round(step.maxMs) + ' pooled=' + round(step.pooledMs) + 'ms（每逻辑步 _Process 耗时）');
    lines.push('FRAME_PROFILE perLogicStepMs 预算=' + round(step.budgetMs) + 'ms (' + step.budgetSource + ') 占用=' + round(step.percentOfBudget) + '%  墙钟校验每步可得=' + round(step.wallClockPerStepMs) + 'ms');
  }
  if (result.perRenderedFrame) lines.push('FRAME_PROFILE perRenderedFrame 间隔=' + round(result.perRenderedFrame.intervalMs) + 'ms 其中_Process=' + round(result.perRenderedFrame.processMs) + 'ms 未归因残差=' + round(result.perRenderedFrame.remainderMs) + 'ms 其中 drawLatency(中位)=' + round(result.perRenderedFrame.drawLatencyMs) + 'ms idleWall(中位)=' + round(result.perRenderedFrame.idleWallMs) + 'ms');
  const invariants = result.invariants;
  if (invariants) lines.push('FRAME_PROFILE 三桶不变量 ' + (invariants.ok ? 'OK' : 'FAILED ' + invariants.violationCount + ' 条') + ' 检查=' + invariants.checked.map(entry => entry.field + '===' + entry.denominator + '(' + entry.windowsChecked + '窗/' + entry.entriesChecked + '项)').join(' '));
  if (invariants && !invariants.ok) for (const violation of invariants.violations.slice(0, 5)) lines.push('FRAME_PROFILE 不变量违规 ' + violation.label + ' probe=' + violation.probe + ' expected=' + violation.expected + ' actual=' + violation.actual + ' frame=' + violation.frame);
  const breakdown = result.processBreakdown;
  if (breakdown && breakdown.unlocked) {
    const seg = entry => entry === null ? 'n/a' : round(entry.meanMs);
    lines.push('FRAME_PROFILE 墙钟分段 period=' + seg(breakdown.period) + 'ms processWall=' + seg(breakdown.processWall) + ' drawLatency=' + seg(breakdown.drawLatency) + ' idle=' + seg(breakdown.idleWall) + ' 闭合残差中位=' + (breakdown.closure === null ? 'n/a' : round(breakdown.closure.medianPercent) + '%') + ' 最差=' + (breakdown.closure === null ? 'n/a' : round(breakdown.closure.worstPercent) + '%'));
    const steps = Object.entries(breakdown.bySteps).sort((a, b) => a[0].localeCompare(b[0]));
    if (steps.length > 0) lines.push('FRAME_PROFILE 按步数 _Process 耗时 ' + steps.map(([name, entry]) => name + '=' + round(entry.meanMs) + 'ms(n' + entry.windows + ')').join(' '));
  } else lines.push('FRAME_PROFILE processBreakdown 缺失（需要 processWall/drawLatency/idleWall 探针）');
  const buckets = result.buckets;
  if (buckets) for (const [bucketName, bucket] of Object.entries(buckets)) {
    if (bucket === null) continue;
    const rows2 = Object.entries(bucket.perCall).map(([name, entry]) => name + '(' + bucket.denominator + ')=' + (bucketName === 'step' ? round(entry.meanMsPerStep?.p50Ms) : round(entry.meanMsPerCall?.p50Ms)) + 'ms/call 共' + round(entry.meanMsPerSecond?.p50Ms) + 'ms/s');
    if (rows2.length > 0) lines.push('FRAME_PROFILE ' + bucketName + '桶 ' + rows2.join(' | '));
  }
  const correlations = result.correlations;
  if (correlations) {
    const pairs = [['perStep~全线程CPU', correlations.perStepVsFullThreadCpu], ['renderInterval~全线程CPU', correlations.renderIntervalVsFullThreadCpu], ['renderInterval~perStep', correlations.renderIntervalVsPerStep], ['renderInterval~alive', correlations.renderIntervalVsAlive], ['alive~renderP95', correlations.aliveVsRenderP95]];
    for (const [label, entry] of pairs) {
      if (!entry || entry.r === null) { lines.push('CORRELATION ' + label + ' 无法计算（样本不足）'); continue; }
      const spread = entry.sensitivity.spread;
      const flags = entry.sensitivity.signStable === false ? '  符号不稳定!' : '';
      lines.push('CORRELATION ' + label + ' r=' + (Math.round(entry.r * 1000) / 1000) + ' n=' + entry.n + ' 样本=' + entry.sampleRange + ' 剔除离群=' + entry.excludedOutliers + ' 敏感性r∈[' + (spread === null ? 'n/a' : Math.round(spread.min * 1000) / 1000 + ',' + Math.round(spread.max * 1000) / 1000) + ']' + flags);
    }
    lines.push('CORRELATION 说明: 每条都附 n/样本范围/warmup/敏感性；符号随口径翻转的不得作结论');
  }
  lines.push('CONCLUSION alive: ' + result.criteria.aliveGrowth.verdict);
  lines.push('CONCLUSION 相关性: ' + result.criteria.correlation.verdict);
  lines.push('CONCLUSION 渲染预算: ' + result.criteria.renderBudget.verdict);
  if (result.unanswered.length > 0) lines.push('FRAME_PROFILE 本次测量无法回答: ' + result.unanswered.join('；'));
  else lines.push('FRAME_PROFILE 本次测量覆盖全部判据');
  return lines.join('\n');
}

async function main() {
  let captureInfo = { logFile: replay, liveLine: null, frames: 0, command: 'replay ' + replay };
  if (replay === null) {
    captureInfo = await capture();
    if (captureInfo.logFile === null) throw new Error('no performance-*.jsonl produced under ' + path.join(runRoot, 'user', 'logs'));
  }
  const result = await analyze(captureInfo.logFile);
  result.capture = { logFile: captureInfo.logFile, liveLine: captureInfo.liveLine, liveFrames: captureInfo.frames, command: captureInfo.command };
  await mkdir(output, { recursive: true });
  const target = path.join(output, label + '.json');
  await writeFile(target, JSON.stringify(result, null, 2));
  console.log(summarize(result));
  console.log('FRAME_PROFILE_OK label=' + result.label + ' json=' + target);
}
main().catch(error => {
  console.error('FRAME_PROFILE_FAILED ' + (error instanceof Error ? error.message : String(error)));
  process.exitCode = 1;
});