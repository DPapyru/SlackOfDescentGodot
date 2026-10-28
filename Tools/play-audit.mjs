import { prepareDiagnostics } from './diagnostics.mjs';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';

const project = path.resolve(process.env.SLACK_PLAY_PROJECT ?? path.join(import.meta.dirname, '..'));
prepareDiagnostics(project);
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const output = path.resolve(process.env.SLACK_PLAY_OUTPUT ?? path.join(project, '.playtest', 'play'));
const only = (process.argv[2] ?? 'all').replace(/^--only=?/, '');
if (!['play', 'testroom', 'map', 'character', 'editors', 'all'].includes(only)) throw new Error('Use --only play|testroom|map|character|editors|all');
const stages = only === 'all' ? ['play', 'map', 'character'] : only === 'editors' ? ['map', 'character'] : [only];
const shots = [];
await mkdir(output, { recursive: true });
async function run(stage) {
  const first = shots.length;
  const args = ['--path', project, '--audio-driver', 'Dummy', '--log-file', path.join(output, stage + '.log'), 'res://Tools/PlayAudit.tscn'];
  if (stage === 'testroom') args.push('--', '--testroom');
  else if (stage !== 'play') args.push('--', '--editors', '--only', stage);
  let log = '';
  const code = await new Promise((resolve, reject) => {
    const child = spawn(godot, args, { cwd: project, env: { ...process.env, DISPLAY: process.env.DISPLAY ?? ':1', SLACK_PLAY_OUTPUT: output, SLACK_USER_ROOT: path.join(output, 'user'), SLACK_PERF: '0' }, stdio: ['ignore', 'pipe', 'pipe'] });
    const timeout = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('Timeout ' + stage)); }, 600000);
    child.stdout.on('data', data => { log += data; process.stdout.write(data); });
    child.stderr.on('data', data => { log += data; process.stderr.write(data); });
    child.on('error', reject);
    child.on('exit', status => { clearTimeout(timeout); resolve(status ?? 1); });
  });
  for (const line of log.split('\n')) {
    const shot = /PLAY_AUDIT shot=(\S+)/.exec(line);
    if (shot) shots.push(path.join(output, shot[1] + '.png'));
    if (/PLAY_AUDIT_FAILED|PLAY_AUDIT defect=|Exception|Timed out/.test(line)) throw new Error(stage + ': ' + line.trim());
  }
  if (code !== 0) throw new Error(stage + ' exited ' + code);
  if (stage === 'play' || stage === 'testroom') {
    const reached = stage === 'play' ? 'PLAY_AUDIT step=operated' : 'PLAY_AUDIT step=testroom';
    if (!log.includes('PLAY_AUDIT_OK')) throw new Error(stage + ' never printed PLAY_AUDIT_OK');
    if (!log.includes(reached)) throw new Error(stage + ' printed PLAY_AUDIT_OK without reaching ' + reached);
  }
  else if (!log.includes('PLAY_AUDIT editor_checks=')) throw new Error(stage + ' never completed its checks');
  const prefixes = { play: ['play-'], testroom: ['play-'], map: ['editor-map-'], character: ['editor-character-'] };
  const wanted = prefixes[stage];
  if (!wanted) throw new Error(stage + ' has no screenshot prefix');
  if (!shots.slice(first).some(shot => wanted.some(prefix => shot.includes(prefix)))) throw new Error(stage + ' produced no screenshots');
  return stage;
}
const done = [];
for (const stage of stages) done.push(await run(stage));
console.log('PLAY_AUDIT_STAGES_OK ' + done.join(','));
for (const file of [...new Set(shots)]) console.log('PLAY_AUDIT_SHOT ' + file);