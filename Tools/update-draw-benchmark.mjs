import { prepareDiagnostics, runDiagnostic, readDiagnosticResult, removeDiagnosticResult } from './diagnostics.mjs';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
prepareDiagnostics(root);
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
    removeDiagnosticResult(result);
    runDiagnostic(godot, args, { project: root, env, timeout: 900000, stdio: 'inherit' });
    return readDiagnosticResult(result);
}

const report = await run();
const { inline, separated, environment } = report;
if (inline.logicSteps !== separated.logicSteps) throw new Error('logic step counts differ between phases');
for (const phase of [inline, separated]) {
    if (phase.builds !== phase.logicSteps || phase.buildsPerLogicStep !== 1)
        throw new Error('every logic step must execute Draw exactly once');
}
if (separated.coalesced !== separated.logicSteps - separated.presentations)
    throw new Error('coalesced count does not match pending presentations');
if (separated.published !== separated.presentations || separated.rendered !== separated.presentations)
    throw new Error('every presentation must consume one pending frame');
const summary = {
    room: report.roomName,
    stepsPerFrame: report.stepsPerFrame,
    inline: { builds: inline.builds, buildsPerLogicStep: inline.buildsPerLogicStep, stepP50Ms: inline.step.p50Ms, stepP95Ms: inline.step.p95Ms, frameP95Ms: inline.frame.p95Ms },
    separated: { builds: separated.builds, buildsPerLogicStep: separated.buildsPerLogicStep, stepP50Ms: separated.step.p50Ms, stepP95Ms: separated.step.p95Ms, frameP95Ms: separated.frame.p95Ms, coalesced: separated.coalesced },
    environment
};
console.log('UPDATE_DRAW_BENCHMARK_OK ' + JSON.stringify(summary));
console.log('UPDATE_DRAW_BENCHMARK_DIR ' + output);
