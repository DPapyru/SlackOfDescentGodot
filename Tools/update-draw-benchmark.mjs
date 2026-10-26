import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const godot = process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const output = path.resolve(process.env.SLACK_BENCH_DIR ?? path.join(root, '.playtest', 'update-draw-benchmark'));
const frames = Number(process.env.SLACK_BENCH_FRAMES ?? 120);
const stepsPerFrame = Number(process.env.SLACK_BENCH_STEPS ?? 8);
const room = process.env.SLACK_BENCH_ROOM ?? '10';
fs.mkdirSync(output, { recursive: true });

async function run() {
    const result = path.join(output, 'benchmark.json');
    const log = path.join(output, 'godot.log');
    const user = path.join(output, 'user');
    const args = ['--headless', '--path', root, '--audio-driver', 'Dummy', '--log-file', log, '--',
        '--audit', '--dialog-choice', '1', '--input-text', 'Ramda', '--room', room, '--frames', '30',
        '--update-draw-benchmark', String(frames), String(stepsPerFrame)];
    const env = { ...process.env, SLACK_USER_ROOT: user, SLACK_PERF: '0', SLACK_BENCH_OUTPUT: result };
    return await new Promise((resolve, reject) => {
        const child = spawn(godot, args, { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
        let stdout = '', stderr = '';
        const timer = setTimeout(() => { child.kill('SIGKILL'); reject(new Error('update/draw benchmark timed out')); }, 900000);
        child.stdout.on('data', data => { stdout += data; });
        child.stderr.on('data', data => { stderr += data; });
        child.on('error', reject);
        child.on('close', code => {
            clearTimeout(timer);
            if (code !== 0) return reject(new Error('godot exited ' + code + '\n' + stderr));
            if (!/^ERROR:/m.test(stderr) === false) return reject(new Error('godot reported errors\n' + stderr));
            if (!fs.existsSync(result)) return reject(new Error('benchmark output missing\n' + stdout + stderr));
            resolve(JSON.parse(fs.readFileSync(result, 'utf8')));
        });
    });
}

const report = await run();
const { inline, separated, environment } = report;
if (inline.logicSteps !== separated.logicSteps) throw new Error('logic step counts differ between phases');
if (separated.builds >= inline.builds) throw new Error(`separation did not reduce builds: ${separated.builds} >= ${inline.builds}`);
const expectedPerStep = 1 / report.stepsPerFrame;
if (Math.abs(separated.buildsPerLogicStep - expectedPerStep) > 0.02)
    throw new Error(`builds per logic step ${separated.buildsPerLogicStep} does not reflect ${report.stepsPerFrame} steps per frame`);
if (separated.coalesced !== inline.logicSteps - separated.builds)
    throw new Error('coalesced count does not match discarded builds');
if (separated.published !== separated.builds || separated.rendered !== separated.builds)
    throw new Error('every separated frame must publish and render exactly once');
if (separated.p95Ms >= inline.p95Ms) throw new Error('separated render phase is slower at p95');
const summary = {
    room: report.roomName,
    stepsPerFrame: report.stepsPerFrame,
    inline: { builds: inline.builds, buildsPerLogicStep: inline.buildsPerLogicStep, stepP50Ms: inline.step.p50Ms, stepP95Ms: inline.step.p95Ms, frameP95Ms: inline.frame.p95Ms },
    separated: { builds: separated.builds, buildsPerLogicStep: separated.buildsPerLogicStep, stepP50Ms: separated.step.p50Ms, stepP95Ms: separated.step.p95Ms, frameP95Ms: separated.frame.p95Ms, coalesced: separated.coalesced },
    environment
};
console.log('UPDATE_DRAW_BENCHMARK_OK ' + JSON.stringify(summary));
console.log('UPDATE_DRAW_BENCHMARK_DIR ' + output);
