import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';

const project = path.resolve(process.env.SLACK_MAP_PROJECT ?? path.join(import.meta.dirname, '..'));
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const output = process.env.SLACK_MAP_OUTPUT ?? path.join(os.tmpdir(), `ccgame-map-${Date.now()}`);
const mode = process.argv[2] ?? 'data';
await mkdir(output, { recursive: true });
async function run(kind, name, count, rendered = false) {
  const directory = path.join(output, name); await mkdir(directory, { recursive: true });
  const args = [...(rendered ? [] : ['--headless']), '--path', project, '--audio-driver', 'Dummy', '--log-file', path.join(directory, 'godot.log'), 'res://Tools/NativeMapAudit.tscn', '--', kind, String(count)];
  let log = '';
  await new Promise((resolve, reject) => {
    const child = spawn(godot, args, { cwd: project, env: { ...process.env, SLACK_MAP_OUTPUT: directory, SLACK_PERF: '0' }, stdio: ['ignore', 'pipe', 'pipe'] });
    const timeout = setTimeout(() => { child.kill('SIGTERM'); reject(new Error(`Timeout ${name}`)); }, 120000);
    child.stdout.on('data', data => { log += data; process.stdout.write(data); }); child.stderr.on('data', data => { log += data; process.stderr.write(data); });
    child.on('error', reject); child.on('exit', code => { clearTimeout(timeout); code === 0 ? resolve() : reject(new Error(`${name} exited ${code}`)); });
  });
  await writeFile(path.join(directory, 'process.log'), log);
  return JSON.parse(await readFile(path.join(directory, 'result.json'), 'utf8'));
}
if (mode === 'loads') {
  const cold = [];
  for (let i = 0; i < 5; i++) cold.push(await run('cold', `cold-${i}`, 1));
  const hot = await run('loads', 'hot', 5);
  const times = [...cold.flatMap(r => r.times), ...hot.times];
  if (times.some(t => t >= 3000)) throw new Error('Complete map load exceeded 3 seconds');
  await writeFile(path.join(output, 'loads.json'), JSON.stringify({ cold, hot, passed: true }, null, 2));
  console.log(`MAP_LOADS_OK cold=5 hot=5 max_ms=${Math.max(...times).toFixed(2)} ${output}`);
} else if (mode === 'performance') {
  const baseline = await run('baseline', 'baseline', 600, true);
  const custom = await run('custom', 'custom', 600, true);
  const ratio = custom.p95Milliseconds / baseline.p95Milliseconds;
  const summary = { baseline, custom, p95Ratio: ratio, withinFivePercent: ratio <= 1.05, note: 'Content/renderer comparison; native 60Hz adapter acceptance must run through the integrated GameHost scheduler.' };
  await writeFile(path.join(output, 'performance.json'), JSON.stringify(summary, null, 2));
  if (ratio > 1.05) throw new Error(`Custom map P95 regressed ${(100 * (ratio - 1)).toFixed(2)}%`);
  console.log(`MAP_PERFORMANCE_OK p95_ratio=${ratio.toFixed(4)} baseline_fps=${baseline.renderFps.toFixed(2)} custom_fps=${custom.renderFps.toFixed(2)} ${output}`);
} else if (mode === 'ui') {
  await run('ui', 'ui', 1, true); console.log(`MAP_UI_FINISHED ${output}`);
} else if (mode === 'collision') {
  await run('collision', 'collision', 1); console.log(`MAP_COLLISION_OK ${output}`);
} else {
  throw new Error('Use loads, performance, collision or ui; pure data: dotnet run --project Tests/Map/NativeMapTests.csproj');
}
