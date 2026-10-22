import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'GameData/manifest.json')));
const code = JSON.parse(fs.readFileSync(path.join(root, 'GameData/code.json')));
const analysis = JSON.parse(fs.readFileSync(path.join(root, 'GameData/analysis.json')));
const media = JSON.parse(fs.readFileSync(path.join(root, 'GameData/media.json')));
assert.equal(analysis.errors.length, 0);
for (const group of ['sprites', 'backgrounds', 'sounds', 'objects', 'rooms', 'scripts', 'fonts']) {
    const ids = manifest[group].map(a => a.id);
    assert.equal(ids.length, new Set(ids).size, group);
}
for (const script of manifest.scripts) assert.ok(code[script.code], script.name);
for (const file of manifest.external) assert.ok(code[file.code], file.path);
for (const [name, asset] of Object.entries(media)) for (const frame of asset.frames) assert.ok(fs.existsSync(path.join(root, 'GameData', frame.path)), name + ':' + frame.path);
for (const background of manifest.backgrounds) assert.ok(fs.existsSync(path.join(root, 'GameData', background.path)), background.path);
for (const font of manifest.fonts) assert.ok(fs.existsSync(path.join(root, 'GameData', font.path)), font.path);
for (const sprite of manifest.sprites) for (const frame of sprite.frames) assert.ok(fs.existsSync(path.join(root, 'GameData', frame.path)), frame.path);
for (const sound of manifest.sounds) if (sound.present) assert.ok(fs.existsSync(path.join(root, 'GameData', sound.path)), sound.path);
for (const room of manifest.rooms) {
    assert.ok(code[room.code], room.name);
    for (const instance of room.instances) {
        assert.ok(manifest.objects.some(o => o.id === instance.object), `${room.name}:${instance.object}`);
        assert.ok(code[instance.code], instance.code);
    }
}
let variables = 0;
for (const object of manifest.objects) for (const event of object.events) for (const action of event.actions) {
    if (action.code) assert.ok(code[action.code], action.code);
    if (action.kind === 6) {
        assert.equal(code[action.code][0], 'group');
        assert.ok(code[action.code][1].length > 0, action.code);
        assert.equal(code[action.code][1][0][1].startsWith('Assign'), true, action.code);
        variables++;
    }
}
assert.equal(variables, 296);
const files = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], { cwd: root, maxBuffer: 16 * 1024 * 1024 }).toString().split('\0').filter(Boolean);
for (const file of files) {
    if (file.startsWith('GameData/assets/Chara/')) continue;
    assert.ok(file === 'docs/requirements.md' || !/\.(md|rst|adoc|txt)$/i.test(file), file);
    if (/\.(cs|rs|mjs)$/.test(file)) {
        const text = fs.readFileSync(path.join(root, file), 'utf8');
        assert.ok(!/^\s*(\/\/|\/\*|\* |<!--)/m.test(text), file);
    }
    if (file.endsWith('.godot')) assert.ok(!/^\s*[#;]/m.test(fs.readFileSync(path.join(root, file), 'utf8')), file);
}
console.log(JSON.stringify({ ...manifest.counts, variableActions: variables, branchContent: 'PASS' }));
