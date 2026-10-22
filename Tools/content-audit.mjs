import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const godot = process.env.GODOT_BIN;
if (!godot) throw new Error('GODOT_BIN required');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'GameData/manifest.json')));
const output = process.env.SLACK_AUDIT_OUTPUT ?? path.join(os.tmpdir(), 'ccgame-content-results.json');
const suite = process.argv[2] ?? 'all';
const filter = process.argv[3] ? new RegExp(process.argv[3]) : null;
const jobs = [];
function loader(room) {
    const name = room.name;
    if (/Loading/.test(name) || room.id <= 1) return room.id;
    if (/^Edit/.test(name)) return 3;
    if (/^EnemyTestRoom2/.test(name)) return 8;
    if (/^EnemyTestRoom/.test(name)) return 5;
    if (/^Playground/.test(name)) return 10;
    if (/^SurvivalWorld/.test(name)) return 18;
    if (/^Survival/.test(name)) return 16;
    const stage = name.match(/(?:Hard_)?Stage(\d)/);
    if (stage) {
        const candidates = manifest.rooms.filter(r => /Loading/.test(r.name) && r.id <= room.id && r.name.includes('Stage' + stage[1]) && /Hard/.test(r.name) === /Hard/.test(name));
        return candidates.at(-1)?.id ?? 80;
    }
    return 22;
}
if (suite === 'all' || suite === 'rooms') for (const room of manifest.rooms) {
    if (filter && !filter.test(room.name)) continue;
    const prepare = loader(room);
    const args = ['--room', String(prepare), '--frames', '30', '--content-frames', '30'];
    if (prepare !== room.id) args.push('--visit-room', String(room.id));
    jobs.push({ kind: 'room', name: room.name, id: room.id, prepare, args });
}
if (suite === 'all' || suite === 'enemies') for (const file of manifest.external) {
    const match = file.path.match(/^Chara\/(.+)\/set_data\.txt$/);
    if (!match || (filter && !filter.test(match[1]))) continue;
    const bossRooms = { BossMachine: 41, BossParasite: 53 };
    const bossRoom = bossRooms[match[1]];
    jobs.push({ kind: 'enemy', name: match[1], args: bossRoom ? ['--room', String(bossRoom), '--frames', '210'] : ['--room', '10', '--frames', '30', '--enemy-audit', match[1]] });
}
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ccgame-content-'));
const results = [];
async function run(job, index) {
    const user = path.join(temporary, String(index));
    return await new Promise(resolve => {
        const started = Date.now();
        const child = spawn(godot, ['--headless', '--path', root, '--log-file', path.join(temporary, index + '.log'), '--', '--audit', '--dialog-choice', '1', '--input-text', 'Ramda', ...job.args], {
            env: { ...process.env, SLACK_USER_ROOT: user, SLACK_PERF: '0' },
        });
        let stdout = '', stderr = '', timedOut = false;
        const timer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, 120000);
        child.stdout.on('data', data => { stdout += data; });
        child.stderr.on('data', data => { stderr += data; });
        child.on('error', error => { stderr += error.message; });
        child.on('close', code => {
            clearTimeout(timer);
            const passed = code === 0 && !timedOut && stdout.includes('AUDIT_OK') && !/^ERROR:/m.test(stderr);
            console.log(`${job.kind} ${job.name} ${passed ? 'PASS' : 'FAIL'} ${stderr.match(/^ERROR:.*$/m)?.[0] ?? ''}`);
            resolve({ ...job, code, passed, timedOut, seconds: (Date.now() - started) / 1000, stdout, stderr });
        });
    });
}
let next = 0;
await Promise.all(Array.from({ length: Number(process.env.SLACK_AUDIT_WORKERS ?? 2) }, async () => {
    while (next < jobs.length) {
        const index = next++;
        results.push(await run(jobs[index], index));
        fs.writeFileSync(output, JSON.stringify({ temporary, results }, null, 2));
    }
}));
console.log(JSON.stringify({ output, passed: results.filter(r => r.passed).length, total: jobs.length }));
if (results.some(r => !r.passed)) process.exitCode = 1;
