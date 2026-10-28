import { prepareDiagnostics } from './diagnostics.mjs';
import { spawn } from 'node:child_process';
import { mkdir, rm, writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const project = path.resolve(import.meta.dirname, '..');
prepareDiagnostics(project);
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const outRoot = path.join(project, '.playtest', 'allmaps');
const args = process.argv.slice(2);

function option(name, fallback) {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : fallback;
}

const rooms = option('--rooms', '').split(',').map(value => value.trim()).filter(value => value.length > 0).map(Number);
const frames = Number(option('--frames', '120'));
const timeoutSeconds = Number(option('--timeout', '60'));
const entryRoomOverride = option('--entry-room', null) === null ? null : Number(option('--entry-room', null));
const reportPath = path.join(outRoot, option('--out', 'report.json'));

const crashPatterns = [
  { name: 'NullReferenceException', pattern: /NullReferenceException/ },
  { name: 'Uninitialized variable', pattern: /Uninitialized variable/ },
  { name: 'Mixed add', pattern: /Mixed add/i },
  { name: 'SCRIPT ERROR', pattern: /SCRIPT ERROR/ },
  { name: 'InvalidOperationException', pattern: /InvalidOperationException/ },
  { name: 'IndexOutOfRange', pattern: /IndexOutOfRangeException/ },
  { name: 'KeyNotFoundException', pattern: /KeyNotFoundException/ },
  { name: 'ArgumentOutOfRange', pattern: /ArgumentOutOfRangeException/ }
];

async function runRoom(room) {
  const entryRoom = entryRoomOverride === null ? room : entryRoomOverride;
  const expectedRoom = entryRoomOverride === null ? room : room;
  const runRoot = path.join(outRoot, 'room-' + room);
  await rm(runRoot, { recursive: true, force: true });
  await mkdir(runRoot, { recursive: true });
  const logFile = path.join(runRoot, 'godot.log');
  const inner = [
    'SLACK_PERF=1',
    'DISPLAY=' + (process.env.DISPLAY ?? ':1'),
    'SLACK_USER_ROOT=' + JSON.stringify(path.join(runRoot, 'user')),
    JSON.stringify(godot),
    '--path', JSON.stringify(project),
    '--audio-driver Dummy',
    '--log-file', JSON.stringify(logFile),
    '--fullscreen',
    'res://Main.tscn',
    '-- --audit --room ' + entryRoom + ' --frames ' + frames + ' --dialog-choice 1'
  ].join(' ');
  const started = Date.now();
  let timedOut = false;
  const result = await new Promise(resolve => {
    const child = spawn('node', [path.join(project, 'Tools', 'agent-lock.mjs'), 'godot', inner], { cwd: project, env: process.env, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill('SIGKILL');
    }, timeoutSeconds * 1000);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('close', code => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
    child.on('error', error => {
      clearTimeout(timer);
      resolve({ code: -1, stdout, stderr: stderr + '\nSPAWN_ERROR ' + error.message });
    });
  });
  const elapsed = (Date.now() - started) / 1000;
  let logText = '';
  try { logText = await readFile(logFile, 'utf8'); } catch { logText = ''; }
  const combined = result.stdout + '\n' + result.stderr + '\n' + logText;
  const actualMatch = combined.match(/AUDIT room=(\d+)/);
  const actualRoom = actualMatch === null ? null : Number(actualMatch[1]);
  const framesMatch = combined.match(/AUDIT room=\d+ frames=(\d+)/);
  const actualFrames = framesMatch === null ? null : Number(framesMatch[1]);
  const crashes = crashPatterns.filter(entry => entry.pattern.test(combined)).map(entry => entry.name);
  const crashLines = [];
  for (const line of combined.split('\n')) {
    if (crashPatterns.some(entry => entry.pattern.test(line))) crashLines.push(line.trim().slice(0, 300));
  }
  const auditOk = /AUDIT_OK/.test(combined);
  const auditFailed = /AUDIT_FAILED/.test(combined);
  let verdict = 'PASS';
  if (timedOut) verdict = 'TIMEOUT';
  else if (crashes.length > 0 || auditFailed || result.code !== 0) verdict = 'FAIL';
  else if (actualRoom === null) verdict = 'INCONCLUSIVE';
  else if (actualRoom !== room) verdict = 'INCONCLUSIVE';
  else if (!auditOk) verdict = 'INCONCLUSIVE';
  return {
    room,
    entryRoom,
    expectedRoom,
    actualRoom,
    actualFrames,
    requestedFrames: frames,
    exitCode: result.code,
    seconds: Math.round(elapsed * 10) / 10,
    timedOut,
    auditOk,
    auditFailed,
    crashes,
    crashLines: crashLines.slice(0, 8),
    verdict,
    logFile: path.relative(project, logFile)
  };
}

async function main() {
  await mkdir(outRoot, { recursive: true });
  const results = [];
  for (const room of rooms) {
    const entry = await runRoom(room);
    results.push(entry);
    console.log(entry.verdict + ' actual=' + entry.actualRoom + ' frames=' + entry.actualFrames + ' exit=' + entry.exitCode + ' ' + entry.seconds + 's' + (entry.crashes.length > 0 ? ' crashes=' + entry.crashes.join('|') : ''));
  }
  const summary = {
    total: results.length,
    pass: results.filter(entry => entry.verdict === 'PASS').length,
    fail: results.filter(entry => entry.verdict === 'FAIL').length,
    inconclusive: results.filter(entry => entry.verdict === 'INCONCLUSIVE').length,
    timeout: results.filter(entry => entry.verdict === 'TIMEOUT').length
  };
  const report = { generatedAt: new Date().toISOString(), frames, timeoutSeconds, summary, results };
  await writeFile(reportPath, JSON.stringify(report, null, 1));
  console.log('');
  console.log('SUMMARY total=' + summary.total + ' pass=' + summary.pass + ' fail=' + summary.fail + ' inconclusive=' + summary.inconclusive + ' timeout=' + summary.timeout);
  for (const entry of results) {
    if (entry.verdict === 'PASS') continue;
    console.log('DETAIL room=' + entry.room + ' verdict=' + entry.verdict + ' actual=' + entry.actualRoom + ' exit=' + entry.exitCode + (entry.actualRoom !== null && entry.actualRoom !== entry.room ? ' driftedTo=' + entry.actualRoom : ''));
    for (const line of entry.crashLines) console.log('    ' + line);
  }
  console.log('REPORT ' + reportPath);
}

main().catch(error => {
  console.error('ALLMAPS_FAILED ' + error.message);
  process.exitCode = 1;
});