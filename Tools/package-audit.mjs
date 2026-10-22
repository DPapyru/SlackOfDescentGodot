import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

export function files(directory, prefix = '') {
    return fs.readdirSync(path.join(directory, prefix), { withFileTypes: true }).flatMap(entry => {
        const name = prefix ? prefix + '/' + entry.name : entry.name;
        return entry.isDirectory() ? files(directory, name) : [name];
    }).sort();
}
export function fingerprint(file) {
    const bytes = fs.readFileSync(file);
    return { size: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
}
export function writeInventory(directory, target) {
    const inventory = Object.fromEntries(files(directory).filter(name => name !== 'package-files.json').map(name => [name, fingerprint(path.join(directory, name))]));
    fs.writeFileSync(path.join(directory, 'package-files.json'), JSON.stringify({ version: 1, target, files: inventory }, null, 2));
    return inventory;
}
export function auditPackage(directory, source) {
    const inventory = JSON.parse(fs.readFileSync(path.join(directory, 'package-files.json')));
    assert.equal(inventory.version, 1);
    const actual = files(directory).filter(name => name !== 'package-files.json');
    assert.deepEqual(actual, Object.keys(inventory.files).sort(), 'Package file list changed');
    for (const name of actual) assert.deepEqual(fingerprint(path.join(directory, name)), inventory.files[name], 'Package file damaged: ' + name);
    const data = path.join(directory, 'GameData');
    const read = name => JSON.parse(fs.readFileSync(path.join(data, name)));
    const manifest = read('manifest.json'), media = read('media.json'), code = read('code.json');
    const requireFile = name => assert.ok(fs.existsSync(path.join(data, name)), 'Missing packaged resource: ' + name);
    const requireCode = key => assert.ok(code[key], 'Missing packaged code: ' + key);
    for (const name of ['sources.json', 'particles.bin', 'assets/Cfg/KeyConfig', 'assets/Cfg/data', 'assets/Cfg/itemData']) requireFile(name);
    for (const sprite of manifest.sprites) for (const frame of sprite.frames) requireFile(frame.path);
    for (const sprite of Object.values(media)) for (const frame of sprite.frames) requireFile(frame.path);
    for (const group of ['backgrounds', 'fonts', 'sounds']) for (const asset of manifest[group]) if (asset.present !== false) requireFile(asset.path);
    for (const script of manifest.scripts) requireCode(script.code);
    for (const external of manifest.external) requireCode(external.code);
    const characterFiles = actual.filter(name => name.startsWith('GameData/assets/Chara/'));
    assert.ok(characterFiles.length > 0, 'Packaged Chara directory is empty');
    if (source) {
        const expected = files(source).filter(name => !name.endsWith('.import') && path.basename(name) !== 'Thumbs.db');
        assert.deepEqual(actual.filter(name => name.startsWith('GameData/')).map(name => name.slice(9)), expected, 'Packaged GameData file list differs from source');
        for (const name of expected) assert.deepEqual(fingerprint(path.join(source, name)), inventory.files['GameData/' + name], 'Packaged GameData differs from source: ' + name);
    }
    return { result: 'PACKAGE_AUDIT_OK', target: inventory.target, files: actual.length, characterFiles: characterFiles.length, bytes: Object.values(inventory.files).reduce((sum, file) => sum + file.size, 0) };
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    if (!process.argv[2]) throw new Error('Usage: node Tools/package-audit.mjs package-directory [source-GameData]');
    console.log(JSON.stringify(auditPackage(path.resolve(process.argv[2]), process.argv[3] && path.resolve(process.argv[3]))));
}
