import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const source = path.resolve(process.env.SLACK_AUDIT_PROJECT ?? path.join(import.meta.dirname, '..'));
const godot = process.env.GODOT_BIN;
if (!godot) throw new Error('GODOT_BIN required');
if (process.platform === 'linux' && !process.env.DISPLAY && !process.env.WAYLAND_DISPLAY) throw new Error('A visible desktop display is required');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ccgame-menu-window-'));
const project = path.join(temporary, 'project');
const user = path.join(temporary, 'user');
const results = [];
const env = {
    ...process.env,
    SLACK_USER_ROOT: user,
    SLACK_PERF: '0',
    XDG_CACHE_HOME: path.join(temporary, 'cache'),
    XDG_CONFIG_HOME: path.join(temporary, 'config'),
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
};
delete env.SLACK_SOURCE;
fs.mkdirSync(project);
for (const file of ['SlackOfDescent.csproj', 'project.godot', 'Main.tscn']) fs.copyFileSync(path.join(source, file), path.join(project, file));
fs.cpSync(path.join(source, 'Runtime'), path.join(project, 'Runtime'), { recursive: true });
for (const directory of ['GameData', 'Tools', 'Examples']) fs.symlinkSync(path.join(source, directory), path.join(project, directory), process.platform === 'win32' ? 'junction' : 'dir');

async function run(command, args, name) {
    const log = path.join(temporary, name + '.log');
    let stdout = '', stderr = '', timedOut = false, spawnError = '';
    const code = await new Promise(resolve => {
        const child = spawn(command, args, { cwd: project, env, stdio: ['ignore', 'pipe', 'pipe'] });
        const timer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, 120000);
        child.stdout.on('data', bytes => { stdout += bytes; process.stdout.write(bytes); });
        child.stderr.on('data', bytes => { stderr += bytes; process.stderr.write(bytes); });
        child.on('error', error => { spawnError = error.message; });
        child.on('close', exitCode => { clearTimeout(timer); resolve(exitCode); });
    });
    fs.writeFileSync(log, stdout + stderr + spawnError);
    results.push({ name, code, timedOut, spawnError, log });
    if (code !== 0 || timedOut || spawnError) throw new Error(name + ' failed; see ' + log);
    return stdout + stderr;
}

let passed = false, failure = '';
try {
    const packages = process.env.NUGET_PACKAGES ?? path.join(os.homedir(), '.nuget', 'packages');
    await run('dotnet', ['restore', 'SlackOfDescent.csproj', '--source', packages, '-p:NuGetAudit=false'], 'restore');
    await run('dotnet', ['build', 'SlackOfDescent.csproj', '--no-restore', '-p:IncludeDiagnostics=true'], 'build');
    for (const reload of [false, true]) {
        const name = reload ? 'reload' : 'menus';
        const engineLog = path.join(temporary, name + '-engine.log');
        const args = ['--path', project, '--windowed', '--audio-driver', 'Dummy', '--log-file', engineLog, '--', '--audit', '--native-integration-audit', '--native-menu-only', '--native-menu-window', '--dialog-choice', '1'];
        if (reload) args.push('--native-menu-reload');
        const output = await run(godot, args, name);
        const text = output + fs.readFileSync(engineLog, 'utf8');
        const marker = reload ? 'NATIVE_MENU_RELOAD_OK language=en' : 'NATIVE_MENU_WINDOW_OK editors=3 returns=3 language_saved=en';
        if (!text.includes(marker) || !text.includes('WINDOW_RENDERED ') || /^ERROR:/m.test(text)) throw new Error(name + ' did not pass visible rendering checks');
        if (!reload && !text.includes('NATIVE_MENU_OK ')) throw new Error('Menu assertions did not finish');
    }
    passed = true;
} catch (error) {
    failure = error.message;process.exitCode = 1;
} finally {
    const report = path.join(temporary, 'result.json');
    fs.writeFileSync(report, JSON.stringify({ passed, failure, project, user, results }, null, 2));
    console.log((passed ? 'MENU_WINDOW_AUDIT_OK ' : 'MENU_WINDOW_AUDIT_FAILED ') + report);
    if (failure) console.error(failure);
}
