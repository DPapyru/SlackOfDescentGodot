import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const root = path.resolve(process.env.SLACK_AUDIT_PROJECT ?? path.join(import.meta.dirname, '..'));
const godot = process.env.GODOT_BIN;
if (!godot) throw new Error('GODOT_BIN required');
const code = JSON.parse(fs.readFileSync(path.join(root, 'GameData/code.json')));
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'GameData/manifest.json')));
const pattern = process.argv.slice(2).find(arg => !arg.startsWith('--'));
const selected = pattern ? new RegExp(pattern) : null;
const rendered = process.argv.includes('--rendered');
const output = process.env.SLACK_AUDIT_OUTPUT ?? path.join(os.tmpdir(), 'ccgame-bind-results.json');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ccgame-bind-'));
const results = [];
const fixtures = {
    BossFrog: 'startBind(col,BIND_ROOT,0,col.BINDED_BOSSFROG_VORE_FOOT,0,1);MotionChange(VORE_FOOT,0,1);',
    BossFrog2: 'startBind(col,BIND_ROOT,0,col.BINDED_BOSSFROG_VORE_FOOT,0,1);MotionChange(VORE_FOOT,0,1);',
    BossParasite: 'startBind(col,BIND_CATCH_START,0,col.BINDED_BOSSPARASITE_CATCH_LEFT_START,0,1);MotionChange(CATCH_LEFT_START,0,1);',
    Haruki: 'startBind(col,BIND_CATCH,0,col.BINDED_HARUKI_FINISH,0,1);MotionChange(WALK_FINISH,0,1);',
    Hitokari: 'startBind(col,0,BIND_TACKLE_FINISH,col.BINDED_SLIME_MEDIUM_ATTACKHIT,0,1);MotionChange(TACKLE_FINISH,0,1);',
    Mount: 'startBind(col,0,BIND_MOUNTPUNCH,col.BINDED_MOUNT_MOUNTPUNCH,0,1);MotionChange(MOUNTPUNCH,0,1);',
    Unix: 'startBind(col,BIND_CATCH,0,col.BINDED_UNIX_FINISH,0,1);MotionChange(TOGETTERFINISH,0,1);',
};
function calls(node, name, found = []) {
    if (!Array.isArray(node)) return found;
    if (node[0] === 'call' && node[1] === name) found.push(node);
    for (const child of node) calls(child, name, found);
    return found;
}
function scenario(tree) {
    let result;
    function walk(node) {
        if (!Array.isArray(node) || result) return;
        if (node[0] === 'group') {
            const bind = calls(node, 'startBind')[0];
            const motion = calls(node, 'MotionChange').find(call => call[2][0]?.[0] === 'id');
            if (bind && motion && node[1].some(statement => statement === bind)) {
                const args = structuredClone(bind[2]);
                args[0] = ['num', 0];
                result = { arguments: args, motion: motion[2][0] };
                return;
            }
        }
        for (const child of node) walk(child);
    }
    walk(tree);
    return result;
}
for (const file of manifest.external) {
    const match = file.path.match(/^Chara\/([^/]+)\/step\.txt$/);
    if (!match || selected && !selected.test(match[1])) continue;
    const name = match[1], tree = code[file.code];
    const hasBind = calls(tree, 'startBind').length > 0 || name === 'VorePlantOriginal';
    if (!hasBind) continue;
    const setup = name === 'VorePlantOriginal' ? { collision: true } : fixtures[name] ? { setup: fixtures[name] } : scenario(tree);
    if (!setup) {
        results.push({ name, passed: false, reason: 'No complete binding scenario could be derived; manual scenario required' });
        continue;
    }
    const room = { BossMachine: 41, BossParasite: 53 }[name] ?? 10;
    const args = [ ...(rendered ? [] : ['--headless']), '--path', root, '--log-file', path.join(temporary, name + '.log'), '--', '--audit', '--room', String(room), '--frames', '30', '--dialog-choice', '1', '--bind-scenario', JSON.stringify(setup), ...(rendered ? ['--animation-audit', 'bind:' + name] : ['--bind-audit', name]) ];
    const result = await new Promise(resolve => {
        const started = Date.now();
        const child = spawn(godot, args, { env: { ...process.env, SLACK_USER_ROOT: path.join(temporary, name), SLACK_PERF: process.env.SLACK_PERF ?? '0' } });
        let stdout = '', stderr = '', timedOut = false;
        const timer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, 180000);
        child.stdout.on('data', bytes => { stdout += bytes; });
        child.stderr.on('data', bytes => { stderr += bytes; });
        child.on('error', error => { stderr += error.message; });
        child.on('close', exitCode => {
            clearTimeout(timer);
            resolve({ name, scenario: setup, passed: exitCode === 0 && !timedOut && stdout.includes('CONTENT_BIND_OK') && !/^ERROR:/m.test(stderr), exitCode, timedOut, seconds: (Date.now() - started) / 1000, stdout, stderr });
        });
    });
    results.push(result);
    console.log('BIND ' + name + ' ' + (result.passed ? 'PASS' : 'FAIL') + ' ' + (result.stderr.match(/^ERROR:.*$/m)?.[0] ?? ''));
    fs.writeFileSync(output, JSON.stringify({ temporary, rendered, results }, null, 2));
}
fs.writeFileSync(output, JSON.stringify({ temporary, rendered, results }, null, 2));
console.log(JSON.stringify({ output, passed: results.filter(result => result.passed).length, total: results.length }));
if (!results.length || results.some(result => !result.passed)) process.exitCode = 1;
