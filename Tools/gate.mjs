import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const logDir = path.join(root, '.playtest/gate');
const snapshotPath = path.join(root, 'Tools/gate-expectations.json');
fs.mkdirSync(logDir, { recursive: true });
const only = process.argv.find(arg => arg.startsWith('--only='))?.slice(7) ?? null;
const record = process.argv.includes('--record');
const noise = /^NU\d+$/;
const forbidden = /^CS8625$/;
const notRun = 'NOT RUN: Tests/Map/NativeMapTests.csproj, Tests/CharacterEditor/NativeCharacterTests.csproj';
const steps = [
    { name: 'build', lock: 'build', command: 'dotnet build -c Debug --no-incremental' },
    { name: 'tests', lock: 'tests', command: 'dotnet run --project Tests/RuntimeTests.csproj' },
    { name: 'verify', lock: 'build', command: 'node Tools/verify.mjs' },
    { name: 'complexity', lock: 'build', command: 'node Tools/complexity-test.mjs && node Tools/complexity.mjs --since origin/ai' },
];
if (only && !steps.some(step => step.name === only)) throw new Error('usage: node Tools/gate.mjs [--only=build|tests|verify|complexity] [--record]');
const snapshot = fs.existsSync(snapshotPath) ? JSON.parse(fs.readFileSync(snapshotPath, 'utf8')) : { warnings: [] };
const run = (resource, command, log) => {
    const result = spawnSync('node', ['Tools/agent-lock.mjs', resource, command], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
    const text = (result.stdout ?? '') + (result.stderr ?? '');
    fs.writeFileSync(log, text);
    return { code: result.status ?? 1, text };
};
const relative = file => {
    const resolved = path.resolve(root, file.trim().replace(/ \[[^\]]*\]$/, ''));
    return resolved.startsWith(root + path.sep) ? path.relative(root, resolved) : resolved;
};
const diagnostics = text => {
    const entries = [];
    for (const line of text.split(/\r?\n/)) {
        const located = line.match(/^(.*?)\((\d+),(\d+)\): (warning|error) ([A-Z]+\d+): (.*?)(?: \[[^\]]*\])?$/);
        if (located) { entries.push({ severity: located[4], code: located[5], where: relative(located[1]) + ':' + located[2] + ':' + located[3], message: located[6].trim() }); continue; }
        const project = line.match(/^(.*?) : (warning|error) ([A-Z]+\d+): (.*?)(?: \[[^\]]*\])?$/);
        if (project) entries.push({ severity: project[2], code: project[3], where: relative(project[1]), message: project[4].trim() });
    }
    return [...new Map(entries.map(entry => [entry.severity + entry.code + entry.where + entry.message, entry])).values()];
};
const results = [];
for (const step of steps) {
    if (only && only !== step.name) continue;
    const started = Date.now();
    const { code, text } = run(step.lock, step.command, path.join(logDir, step.name + '.log'));
    const entries = diagnostics(text);
    const result = { step: step.name, code, seconds: (Date.now() - started) / 1000, text, entries, verdict: 'pass', findings: [] };
    const fail = note => { result.verdict = 'fail'; result.findings.push({ verdict: 'fail', note }); };
    const pending = (note, at) => result.findings.push({ verdict: 'known-pending', note, at: at ? [at] : undefined });
    if (code !== 0) fail('exit code ' + code);
    for (const entry of entries) if (entry.severity === 'error') fail('error ' + entry.code + ' at ' + entry.where);
    if (step.name === 'build') {
        const allowed = new Map();
        for (const entry of snapshot.warnings ?? []) allowed.set(entry.code + ' ' + entry.where, (allowed.get(entry.code + ' ' + entry.where) ?? 0) + 1);
        const observed = new Map();
        for (const entry of entries) {
            if (entry.severity !== 'warning') continue;
            if (noise.test(entry.code)) continue;
            if (forbidden.test(entry.code)) { fail(entry.code + ' is forbidden outright at ' + entry.where); continue; }
            const key = entry.code + ' ' + entry.where;
            observed.set(key, (observed.get(key) ?? 0) + 1);
            const budget = allowed.get(key) ?? 0;
            if ((observed.get(key) ?? 0) > budget) fail(entry.code + ' at ' + entry.where + ' is not in the tracked allow-list');
            else pending(entry.code + ' count ' + observed.get(key) + ' == tracked ' + budget, entry.where);
        }
        for (const key of allowed.keys()) if (!observed.has(key)) pending('stale allow-list entry: ' + key + ' no longer occurs, remove it');
        result.warnings = entries.filter(entry => entry.severity === 'warning').length;
        result.noise = entries.filter(entry => entry.severity === 'warning' && noise.test(entry.code)).length;
    }
    if (step.name === 'tests') {
        const passed = text.match(/^PASS (\d+) assertions$/m);
        result.assertions = passed ? Number(passed[1]) : null;
        if (result.assertions === null) fail('no PASS <n> assertions line');

    }
    if (step.name === 'verify' && !text.includes('"branchContent":"PASS"')) fail('verify did not report branchContent PASS');
    results.push(result);
}
for (const result of results) {
    console.log('GATE step=' + result.step + ' ' + result.verdict + ' exit=' + result.code + ' seconds=' + result.seconds.toFixed(1) + (result.warnings === undefined ? '' : ' warnings=' + result.warnings + ' nuget-noise=' + result.noise));
    for (const entry of result.entries) console.log('GATE raw-' + entry.severity + ' ' + entry.code + ' ' + entry.where + ' ' + entry.message);
    for (const finding of result.findings) console.log('GATE ' + finding.verdict + ' ' + finding.note + (finding.at ? ' at ' + finding.at.join(' ') : ''));
}
if (record) {
    if (only) throw new Error('--record needs every step to run');
    const build = results.find(result => result.step === 'build');
    const tests = results.find(result => result.step === 'tests');
    const warnings = build.entries.filter(entry => entry.severity === 'warning' && !noise.test(entry.code)).map(entry => ({ code: entry.code, where: entry.where }));
    fs.writeFileSync(snapshotPath, JSON.stringify({ warnings }, null, 2) + '\n');
    console.log('GATE recorded ' + path.relative(root, snapshotPath) + ' assertions=' + tests.assertions + ' warnings=' + warnings.length);
}
const failed = results.filter(result => result.verdict === 'fail');
const pending = results.reduce((total, result) => total + result.findings.filter(finding => finding.verdict === 'known-pending').length, 0);
console.log(notRun);
console.log('GATE ' + (failed.length ? 'FAIL failed=' + failed.map(result => result.step).join(',') : 'PASS') + ' steps=' + results.length + ' known-pending=' + pending);
if (failed.length) process.exitCode = 1;
