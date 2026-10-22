import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';

const positional = process.argv.slice(2).filter(arg => !arg.startsWith('--target='));
const builds = path.resolve(positional[0] ?? path.join(import.meta.dirname, '../../ccgame-builds'));
const output = path.resolve(positional[1] ?? path.join(builds, 'regression-results.json'));
const selected = process.argv.find(arg => arg.startsWith('--target='))?.slice(9) ?? (process.platform === 'win32' ? 'Windows' : 'Linux');
const targets = selected === 'both' ? ['Linux', 'Windows'] : [selected];
if (targets.some(target => !['Linux', 'Windows'].includes(target))) throw new Error('Target must be Linux, Windows or both');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ccgame-regression-'));
const results = [];
async function run(command, args, env, timeout = 600000) {
    return await new Promise(resolve => {
        const started = Date.now();
        const child = spawn(command, args, { cwd: temporary, env, detached: true });
        let stdout = '', stderr = '', timedOut = false;
        const timer = setTimeout(() => {
            timedOut = true;
            if (process.platform === 'win32') execFileSync('taskkill', ['/pid', String(child.pid), '/t', '/f']);
            else process.kill(-child.pid, 'SIGKILL');
        }, timeout);
        child.stdout.on('data', data => { stdout += data; process.stdout.write(data); });
        child.stderr.on('data', data => { stderr += data; process.stderr.write(data); });
        child.on('error', error => { stderr += error.message; });
        child.on('close', code => {
            clearTimeout(timer);
            resolve({ code, timedOut, seconds: (Date.now() - started) / 1000, stdout, stderr });
        });
    });
}
try {
    for (const target of targets) {
        const executable = path.join(builds, target, target === 'Linux' ? 'SlackOfDescent.x86_64' : 'SlackOfDescent.exe');
        const user = path.join(temporary, target);
        const wine = target === 'Windows' && process.platform !== 'win32' ? process.env.WINE_BIN : null;
        if (target === 'Windows' && process.platform !== 'win32' && !wine) throw new Error('Windows verification requires Windows or an explicit WINE_BIN');
        const env = { ...process.env, SLACK_USER_ROOT: wine ? 'Z:' + user.replaceAll('/', '\\') : user };
        delete env.SLACK_SOURCE;
        if (wine) env.WINEDEBUG = '-all';
        const args = ['--', '--audit', '--regression-audit'];
        console.log('REGRESSION_TARGET ' + target);
        const result = await run(wine ?? executable, wine ? [executable, ...args] : args, env);
        const passed = result.code === 0 && !result.timedOut && result.stdout.includes('GAMEPLAY_REGRESSION_OK') && !/^ERROR:/m.test(result.stderr);
        results.push({ target, environment: wine ? 'Wine' : target + ' native', passed, ...result });
        console.log('REGRESSION_RESULT ' + target + ' ' + (passed ? 'PASS' : 'FAIL'));
    }
} finally {
    fs.writeFileSync(output, JSON.stringify({ date: new Date().toISOString(), results }, null, 2));
    fs.rmSync(temporary, { recursive: true, force: true });
}
if (results.length !== targets.length || results.some(result => !result.passed)) process.exitCode = 1;
