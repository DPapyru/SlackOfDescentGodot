import { readFile, writeFile } from 'node:fs/promises';

const args = process.argv.slice(2);
function option(name, fallback = null) {
  const index = args.indexOf('--' + name);
  return index < 0 ? fallback : args[index + 1] ?? fallback;
}
const logPath = option('log');
if (!logPath) throw new Error('usage: node Tools/render-profile-summary.mjs --log performance.jsonl [--out summary.json]');
const records = (await readFile(logPath, 'utf8')).split('\n').filter(Boolean).map(JSON.parse);
const room = Number(option('room', '11'));
const warmup = Number(option('warmup', '5'));
const windows = records.filter(r => r.type === 'cadence' && r.room === room).slice(warmup);
if (!windows.length) throw new Error(`no stable cadence windows for room ${room}`);
const phaseNames = [...new Set(windows.flatMap(r => Object.keys(r.drawPhases ?? r.graphics?.drawPhases ?? {})))];
const phase = {};
for (const name of phaseNames) {
  const rows = windows.map(r => (r.drawPhases ?? r.graphics?.drawPhases ?? {})[name]).filter(Boolean);
  const total = rows.reduce((sum, row) => sum + (row.totalMs ?? 0), 0);
  const count = rows.reduce((sum, row) => sum + (row.count ?? 0), 0);
  phase[name] = { windows: rows.length, count, totalMs: total, meanMs: count ? total / count : 0, windowMeanMs: windows.length ? total / windows.length : 0, maxMs: Math.max(...rows.map(row => row.maxMs ?? 0)), p50Ms: rows.length ? rows.reduce((sum, row) => sum + (row.p50Ms ?? 0), 0) / rows.length : 0, p95Ms: rows.length ? rows.reduce((sum, row) => sum + (row.p95Ms ?? 0), 0) / rows.length : 0 };
}
const objects = new Map();
for (const record of windows) for (const object of record.objectProfile?.objects ?? []) {
  const key = object.type;
  const total = object.total ?? {};
  const drawBuild = object.phases?.drawBuild ?? {};
  const previous = objects.get(key) ?? { type: key, category: object.category, activeInstances: object.activeInstances, calls: 0, cpuMs: 0, drawBuildMs: 0, allocatedBytes: 0, maxMs: 0, p50Ms: 0, p95Ms: 0 };
  previous.activeInstances = object.activeInstances;
  previous.calls += total.scopeCalls ?? 0;
  previous.cpuMs += total.cpuMs ?? 0;
  previous.drawBuildMs += total.drawBuildMs ?? 0;
  previous.allocatedBytes += total.allocatedBytes ?? 0;
  previous.maxMs = Math.max(previous.maxMs, drawBuild.maxMs ?? 0);
  previous.p50Ms = Math.max(previous.p50Ms, drawBuild.p50Ms ?? 0);
  previous.p95Ms = Math.max(previous.p95Ms, drawBuild.p95Ms ?? 0);
  objects.set(key, previous);
}
const summary = { log: logPath, room, warmupWindows: warmup, stableWindows: windows.length, drawPipeline: phase, objects: [...objects.values()].sort((a, b) => b.drawBuildMs - a.drawBuildMs), frame: { logicFps: windows.reduce((s, r) => s + (r.logicFps ?? 0), 0) / windows.length, renderFps: windows.reduce((s, r) => s + (r.completedRenderFps ?? 0), 0) / windows.length, drawLatencyMs: windows.reduce((s, r) => s + (r.processBreakdown?.drawLatency?.meanMs ?? 0), 0) / windows.length }, worstWindow: windows.reduce((a, b) => (b.drawPhases?.canvas_draw?.maxMs ?? b.graphics?.drawPhases?.canvas_draw?.maxMs ?? 0) > (a.drawPhases?.canvas_draw?.maxMs ?? a.graphics?.drawPhases?.canvas_draw?.maxMs ?? 0) ? b : a) };
const output = option('out');
if (output) await writeFile(output, JSON.stringify(summary, null, 2) + '\n');
console.log(JSON.stringify(summary, null, 2));
