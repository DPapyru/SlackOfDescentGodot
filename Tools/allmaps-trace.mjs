import { spawn } from 'node:child_process';
import { mkdir, rm, writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const project = path.resolve(import.meta.dirname, '..');
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const outRoot = path.join(project, '.playtest', 'allmaps', 'trace');
const args = process.argv.slice(2);

function option(name, fallback) {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : fallback;
}

const rooms = option('--rooms', '').split(',').map(value => value.trim()).filter(value => value.length > 0).map(Number);
const frames = Number(option('--frames', '600'));
const timeoutSeconds = Number(option('--timeout', '120'));
const reportPath = path.join(outRoot, option('--out', 'trace.json'));

const crashPatterns = [
  { name: 'NullReferenceException', pattern: /NullReferenceException/ },
  { name: 'Uninitialized variable', pattern: /Uninitialized variable/ },
  { name: 'Mixed add', pattern: /Mixed add/i },
  { name: 'SCRIPT ERROR', pattern: /SCRIPT ERROR/ }
];

async function traceRoom(room) {
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
    'res://Main.tscn',
    '-- --audit --room ' + room + ' --frames ' + frames + ' --dialog-choice 1 --trace-rooms'
  ].join(' ');
  const started = Date.now();
  let timedOut = false;
  const result = await new Promise(resolve => {
    const child = spawn('node', [path.join(project, 'Tools', 'agent-lock.mjs'), 'godot', inner], { cwd: project, env: process.env, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, timeoutSeconds * 1000);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('close', code => { clearTimeout(timer); resolve({ code, stdout, stderr }); });
    child.on('error', error => { clearTimeout(timer); resolve({ code: -1, stdout, stderr: stderr + '\nSPAWN_ERROR ' + error.message }); });
  });
  let logText = '';
  try { logText = await readFile(logFile, 'utf8'); } catch { logText = ''; }
  const combined = result.stdout + '\n' + result.stderr + '\n' + logText;
  const sequence = [];
  for (const line of combined.split('\n')) {
    const match = line.match(/TRACE_ROOM frame=(\d+) room=(\d+)/);
    if (match === null) continue;
    const entry = { frame: Number(match[1]), room: Number(match[2]) };
    const last = sequence[sequence.length - 1];
    if (last !== undefined && last.room === entry.room) { last.lastFrame = entry.frame; continue; }
    entry.firstFrame = entry.frame;
    entry.lastFrame = entry.frame;
    sequence.push(entry);
  }
  const visited = [...new Set(sequence.map(entry => entry.room))];
  const loadedMatch = combined.match(/TRACE_LOADED room=(\d+)/);
  const loadedRoom = loadedMatch === null ? null : Number(loadedMatch[1]);
  const finalMatch = combined.match(/TRACE_FINAL room=(\d+)/);
  const finalRoom = finalMatch === null ? null : Number(finalMatch[1]);
  const crashes = crashPatterns.filter(entry => entry.pattern.test(combined)).map(entry => entry.name);
  let state = 'OK';
  if (loadedRoom === null) state = 'LOAD_FAILED';
  else if (loadedRoom !== room) state = 'LOAD_FAILED';
  else if (!visited.includes(room)) state = 'SKIPPED_AT_CREATE';
  else if (finalRoom !== room) state = 'DRIFTED';
  return {
    entryRoom: room,
    requestedFrames: frames,
    framesTraced: sequence.length === 0 ? 0 : sequence[sequence.length - 1].lastFrame - sequence[0].firstFrame + 1,
    loadedRoom,
    finalRoom,
    state,
    visited,
    sequence,
    crashes,
    crashLines: combined.split('\n').filter(line => crashPatterns.some(entry => entry.pattern.test(line))).map(line => line.trim().slice(0, 240)).slice(0, 6),
    exitCode: result.code,
    timedOut,
    seconds: Math.round((Date.now() - started) / 100) / 10,
    logFile: path.relative(project, logFile)
  };
}

async function main() {
  await mkdir(outRoot, { recursive: true });
  const results = [];
  for (const room of rooms) {
    process.stdout.write('TRACE room ' + room + ' ... ');
    const entry = await traceRoom(room);
    results.push(entry);
    console.log(entry.state + ' loaded=' + entry.loadedRoom + ' final=' + entry.finalRoom + ' visited=[' + entry.visited.join(',') + '] frames=' + entry.framesTraced + ' ' + entry.seconds + 's' + (entry.crashes.length > 0 ? ' crashes=' + entry.crashes.join('|') : '') + (entry.timedOut ? ' TIMEOUT' : ''));
  }
  const allVisited = new Set();
  for (const entry of results) for (const room of entry.visited) allVisited.add(room);
  const report = {
    generatedAt: new Date().toISOString(),
    frames,
    entryRooms: rooms,
    results,
    reachable: [...allVisited].sort((a, b) => a - b)
  };
  await writeFile(reportPath, JSON.stringify(report, null, 1));
  console.log('');
  console.log('REACHABLE ' + report.reachable.length + ' rooms: ' + report.reachable.join(','));
  console.log('REPORT ' + reportPath);
}

main().catch(error => {
  console.error('TRACE_FAILED ' + error.message);
  process.exitCode = 1;
});
