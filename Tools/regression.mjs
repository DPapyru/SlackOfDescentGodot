import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';

const positional = process.argv.slice(2).filter(arg => !arg.startsWith('--'));
const builds = path.resolve(positional[0] ?? path.join(import.meta.dirname, '../../ccgame-builds'));
const output = path.resolve(positional[1] ?? path.join(builds, 'regression-results.json'));
const selected = process.argv.find(arg => arg.startsWith('--target='))?.slice(9) ?? (process.platform === 'win32' ? 'Windows' : 'Linux');
const targets = selected === 'both' ? ['Linux', 'Windows'] : [selected];
if (targets.some(target => !['Linux', 'Windows'].includes(target))) throw new Error('Target must be Linux, Windows or both');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ccgame-regression-'));
const results = [];
const saveResults = () => fs.writeFileSync(output, JSON.stringify({ date: new Date().toISOString(), results }, null, 2));
const suites = process.argv.includes('--packages') ? [
    { name: 'resources', args: ['--headless', '--', '--audit', '--package-resources-audit'], marker: 'PACKAGE_RUNTIME_OK' },
    { name: 'graphics', args: ['--', '--audit', '--graphics-audit'], marker: 'GRAPHICS_OK' },
    { name: 'characters', args: ['--', '--audit', '--room', '10', '--frames', '30', '--dialog-choice', '1', '--controls-ui-audit'], marker: 'CONTROLS_UI_OK' },
    { name: 'gameplay', args: ['--', '--audit', '--regression-audit'], marker: 'GAMEPLAY_REGRESSION_OK' },
] : [{ name: 'gameplay', args: ['--', '--audit', '--regression-audit'], marker: 'GAMEPLAY_REGRESSION_OK' }];
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
        const proton = target === 'Windows' && process.platform !== 'win32' ? process.env.PROTON_BIN : null;
        const wine = target === 'Windows' && process.platform !== 'win32' ? process.env.WINE_BIN : null;
        if (target === 'Windows' && process.platform !== 'win32' && !wine && !proton) throw new Error('Windows verification requires Windows, PROTON_BIN or WINE_BIN');
        const windowsPath = value => wine || proton ? 'Z:' + value.replaceAll('/', '\\') : value;
        const compat = process.env.STEAM_COMPAT_DATA_PATH ?? path.join(temporary, 'proton');
        if (proton) fs.mkdirSync(compat, { recursive: true });
        for (const suite of suites) {
            const user = path.join(temporary, target + '-' + suite.name);
            const log = path.join(temporary, target + '-' + suite.name + '.log');
            const env = { ...process.env, SLACK_USER_ROOT: windowsPath(user), SLACK_PERF: '0' };
            delete env.SLACK_SOURCE;
            if (wine || proton) env.WINEDEBUG = '-all';
            if (proton) Object.assign(env, { STEAM_COMPAT_DATA_PATH: compat, SteamAppId: '0', SteamGameId: '0', PROTON_LOG: '0' });
            const args = ['--log-file', windowsPath(log), ...suite.args];
            console.log('REGRESSION_TARGET ' + target + ' ' + suite.name);
            const result = await run(proton ?? wine ?? executable, proton ? ['run', executable, ...args] : wine ? [executable, ...args] : args, env);
            const engineLog = fs.existsSync(log) ? fs.readFileSync(log, 'utf8') : '';
            const passed = result.code === 0 && !result.timedOut && (result.stdout + engineLog).includes(suite.marker) && !/^ERROR:/m.test(result.stderr + '\n' + engineLog);
            results.push({ target, suite: suite.name, environment: proton ? 'Proton' : wine ? 'Wine' : target + ' native', passed, ...result, engineLog });
            saveResults();
            console.log('REGRESSION_RESULT ' + target + ' ' + suite.name + ' ' + (passed ? 'PASS' : 'FAIL'));
        }
    }
} finally {
    saveResults();
    fs.rmSync(temporary, { recursive: true, force: true });
}
if (results.length !== targets.length * suites.length || results.some(result => !result.passed)) process.exitCode = 1;
