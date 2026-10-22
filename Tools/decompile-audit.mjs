import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

const [reference, directory, archive] = process.argv.slice(2);
if (!archive) throw new Error('Usage: node Tools/decompile-audit.mjs reference.gm81 GameData source-archive.json');
const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'manifest.json')));
const sources = JSON.parse(fs.readFileSync(archive));
const checks = {};
function equal(actual, expected, label) {
    assert.deepEqual(actual, expected, label);
    checks[label.split('/')[0]] = (checks[label.split('/')[0]] ?? 0) + 1;
}
function text(bytes) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch { return new TextDecoder('shift_jis').decode(bytes); }
}
class Reader {
    constructor(bytes) { this.bytes = bytes; this.offset = 0; }
    take(count) {
        assert.ok(count >= 0 && this.offset + count <= this.bytes.length, 'truncated reference');
        const result = this.bytes.subarray(this.offset, this.offset + count);
        this.offset += count;
        return result;
    }
    u32() { return this.take(4).readUInt32LE(); }
    i32() { return this.take(4).readInt32LE(); }
    f64() { return this.take(8).readDoubleLE(); }
    bool() { return this.u32() !== 0; }
    string() { return text(this.take(this.u32())); }
    chunk() { return new Reader(zlib.inflateSync(this.take(this.u32()))); }
    version(value) { equal(this.u32(), value, 'format/version'); }
    end() { equal(this.offset, this.bytes.length, 'format/chunk-length'); }
}
const input = new Reader(fs.readFileSync(reference));
input.version(1234321);
const version = input.u32();
equal(version, manifest.version === 'GameMaker8_1' ? 810 : 800, 'format/game-version');
input.take(20);
input.version(800);
input.chunk();
function list(category, read) {
    input.version(800);
    const count = input.u32();
    equal(count, manifest.resourceSlots[category], category + '/slots');
    const exported = new Map((manifest[category] ?? []).map(asset => [asset.id, asset]));
    for (let id = 0; id < count; id++) {
        const reader = input.chunk();
        const present = reader.bool();
        equal(exported.has(id), present, category + '/presence/' + id);
        if (present) read(reader, exported.get(id), category + '/' + id);
        reader.end();
    }
}
function named(reader, asset, label) {
    equal(reader.string(), asset.name, label + '/name');
    reader.take(8);
}
function source(reader, key, label) {
    const original = reader.string();
    equal(original, sources[key] ?? '', label + '/source');
}
function pixels(file) {
    const png = fs.readFileSync(path.join(directory, file));
    equal(png.subarray(0, 8), Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), 'pixels/signature');
    let offset = 8, width, height;
    const data = [];
    while (offset < png.length) {
        const length = png.readUInt32BE(offset), type = png.toString('ascii', offset + 4, offset + 8);
        const bytes = png.subarray(offset + 8, offset + 8 + length);
        if (type === 'IHDR') {
            width = bytes.readUInt32BE(0); height = bytes.readUInt32BE(4);
            equal([...bytes.subarray(8)], [8, 6, 0, 0, 0], 'pixels/encoding');
        }
        if (type === 'IDAT') data.push(bytes);
        offset += length + 12;
    }
    const raw = zlib.inflateSync(Buffer.concat(data)), stride = width * 4;
    equal(raw.length, (stride + 1) * height, 'pixels/length');
    const decoded = Buffer.alloc(stride * height);
    for (let y = 0; y < height; y++) {
        const filter = raw[y * (stride + 1)];
        assert.ok(filter <= 4, 'PNG filter');
        for (let x = 0; x < stride; x++) {
            const left = x >= 4 ? decoded[y * stride + x - 4] : 0;
            const above = y ? decoded[(y - 1) * stride + x] : 0;
            const corner = y && x >= 4 ? decoded[(y - 1) * stride + x - 4] : 0;
            const p = left + above - corner;
            const distances = [Math.abs(p - left), Math.abs(p - above), Math.abs(p - corner)];
            const paeth = distances[0] <= distances[1] && distances[0] <= distances[2] ? left : distances[1] <= distances[2] ? above : corner;
            decoded[y * stride + x] = raw[y * (stride + 1) + x + 1] + [0, left, above, Math.floor((left + above) / 2), paeth][filter];
        }
    }
    for (let i = 0; i < decoded.length; i += 4) [decoded[i], decoded[i + 2]] = [decoded[i + 2], decoded[i]];
    return decoded;
}
list('triggers', () => { throw new Error('Unsupported trigger reference'); });
input.take(8);
input.version(800);
equal(input.u32(), manifest.constants.length, 'constants/count');
for (const item of manifest.constants) {
    equal(input.string(), item.name, 'constants/name');
    equal(input.string(), item.value, 'constants/value');
}
input.take(8);
list('sounds', (r, asset, label) => {
    named(r, asset, label); r.version(800);
    equal(r.u32(), asset.kind, label + '/kind');
    const extension = r.string(); r.string();
    equal(asset.path, `sounds/${asset.id}${extension}`, label + '/path');
    equal(r.bool(), asset.present, label + '/present');
    if (asset.present) {
        const original = r.take(r.u32()), exported = fs.readFileSync(path.join(directory, asset.path));
        if (!original.equals(exported)) {
            const decode = bytes => execFileSync('ffmpeg', ['-v', 'error', '-i', 'pipe:0', '-f', 's16le', '-acodec', 'pcm_s16le', 'pipe:1'], { input: bytes, maxBuffer: 64 * 1024 * 1024 });
            equal(decode(original), decode(exported), label + '/decoded-audio');
        } else checks.audioBytes = (checks.audioBytes ?? 0) + 1;
    }
    const fx = r.u32();
    equal(fx, Object.values(asset.effects).reduce((mask, value, i) => mask | (Number(value) << i), 0), label + '/effects');
    equal(r.f64(), asset.volume, label + '/volume'); equal(r.f64(), asset.pan, label + '/pan'); r.bool();
});
list('sprites', (r, asset, label) => {
    named(r, asset, label); r.version(800);
    equal(r.i32(), asset.originX, label + '/originX'); equal(r.i32(), asset.originY, label + '/originY');
    equal(r.u32(), asset.frames.length, label + '/frames');
    for (const frame of asset.frames) {
        r.version(800); equal(r.u32(), frame.width, label + '/width'); equal(r.u32(), frame.height, label + '/height');
        if (frame.width * frame.height) equal(pixels(frame.path), r.take(r.u32()), label + '/pixel-bytes');
    }
    r.take(32);
});
list('backgrounds', (r, asset, label) => {
    named(r, asset, label); r.version(710); r.take(28); r.version(800);
    equal(r.u32(), asset.width, label + '/width'); equal(r.u32(), asset.height, label + '/height');
    if (asset.width * asset.height) equal(pixels(asset.path), r.take(r.u32()), label + '/pixel-bytes');
});
list('paths', () => { throw new Error('Unsupported path reference'); });
list('scripts', (r, asset, label) => { named(r, asset, label); r.version(800); source(r, asset.code, label); });
list('fonts', (r, asset, label) => {
    named(r, asset, label); r.version(800);
    equal(r.string(), asset.systemName, label + '/system-name'); equal(r.u32(), asset.size, label + '/size');
    equal(r.bool(), asset.bold, label + '/bold'); equal(r.bool(), asset.italic, label + '/italic');
    const range = r.u32();
    equal(range & 65535, asset.rangeStart, label + '/rangeStart');
    if (version === 810) { equal(range >>> 24, asset.aa, label + '/aa'); equal((range >>> 16) & 255, asset.charset, label + '/charset'); }
    equal(r.u32(), asset.rangeEnd, label + '/rangeEnd');
});
list('timelines', () => { throw new Error('Unsupported timeline reference'); });
function action(r) {
    r.version(440);
    const library = r.u32(), id = r.u32(), kind = r.u32(); r.u32();
    const condition = r.bool(), appliesToSomething = r.bool(), execution = r.u32(), fn = r.string(), rawCode = r.string(), parameterCount = r.u32();
    equal(r.u32(), 8, 'actions/type-slots');
    const types = Array.from({ length: 8 }, () => r.u32());
    const target = r.i32(), relative = r.bool(); equal(r.u32(), 8, 'actions/parameter-slots');
    const parameterStrings = Array.from({ length: 8 }, () => r.string());
    const invert = r.bool();
    return { library, id, kind, condition, appliesToSomething, execution, function: fn, rawCode, parameterCount, target, relative, parameterStrings, parameterTypes: types.slice(0, parameterCount), invert };
}
list('objects', (r, asset, label) => {
    named(r, asset, label); r.version(430);
    for (const [property, type] of [['sprite', 'i32'], ['solid', 'bool'], ['visible', 'bool'], ['depth', 'i32'], ['persistent', 'bool'], ['parent', 'i32'], ['mask', 'i32']]) equal(r[type](), asset[property], label + '/' + property);
    const kinds = r.u32() + 1;
    let events = 0;
    for (let kind = 0; kind < kinds; kind++) {
        for (let sub = r.i32(); sub !== -1; sub = r.i32()) {
            r.version(400);
            const count = r.u32(), event = asset.events[events++];
            equal([event.kind, event.sub, event.actions.length], [kind, sub, count], label + '/event');
            for (let i = 0; i < count; i++) {
                const expected = action(r), actual = event.actions[i];
                for (const [key, value] of Object.entries(expected)) equal(actual[key], value, label + '/action/' + i + '/' + key);
                equal(actual.sourceIndex, i, label + '/action-order');
                if (actual.code) {
                    const expectedSource = expected.kind === 6 ? expected.parameterStrings[0] + (expected.relative ? '+=' : '=') + expected.parameterStrings[1] : expected.kind === 7 ? expected.parameterStrings[0] : expected.rawCode;
                    equal(sources[actual.code] ?? '', expectedSource, label + '/action-source');
                }
            }
        }
    }
    equal(events, asset.events.length, label + '/event-count');
});
function compatSource(r, key, label) {
    const original = r.string();
    const marker = '/****************/\r\n\r\n';
    equal(original.startsWith('/* gm8.2 compat */') ? original.slice(original.indexOf(marker) + marker.length) : original, sources[key] ?? '', label + '/source');
}
list('rooms', (r, asset, label) => {
    named(r, asset, label); r.version(541);
    equal(r.string(), asset.caption, label + '/caption');
    equal(r.u32(), asset.width, label + '/width'); equal(r.u32(), asset.height, label + '/height'); r.take(12);
    equal(r.u32(), asset.speed, label + '/speed'); equal(r.bool(), asset.persistent, label + '/persistent');
    equal(r.u32(), asset.colour, label + '/colour'); equal(r.bool(), asset.clearScreen, label + '/clearScreen');
    compatSource(r, asset.code, label);
    equal(r.u32(), asset.backgrounds.length, label + '/background-count');
    for (const item of asset.backgrounds) for (const [key, type] of [['visible', 'bool'], ['foreground', 'bool'], ['background', 'i32'], ['x', 'i32'], ['y', 'i32'], ['tileX', 'bool'], ['tileY', 'bool'], ['hspeed', 'i32'], ['vspeed', 'i32'], ['stretch', 'bool']]) equal(r[type](), item[key], label + '/background/' + key);
    equal(r.bool(), asset.viewsEnabled, label + '/viewsEnabled'); equal(r.u32(), asset.views.length, label + '/view-count');
    for (const item of asset.views) for (const [key, type] of [['visible', 'bool'], ['x', 'i32'], ['y', 'i32'], ['width', 'u32'], ['height', 'u32'], ['portX', 'i32'], ['portY', 'i32'], ['portWidth', 'u32'], ['portHeight', 'u32'], ['hborder', 'i32'], ['vborder', 'i32'], ['hspeed', 'i32'], ['vspeed', 'i32'], ['target', 'i32']]) equal(r[type](), item[key], label + '/view/' + key);
    equal(r.u32(), asset.instances.length, label + '/instance-count');
    for (const item of asset.instances) {
        for (const key of ['x', 'y', 'object', 'id']) equal(r.i32(), item[key], label + '/instance/' + key);
        compatSource(r, item.code, label); r.bool();
    }
    equal(r.u32(), asset.tiles.length, label + '/tile-count');
    for (const item of asset.tiles) {
        for (const [key, type] of [['x', 'i32'], ['y', 'i32'], ['background', 'i32'], ['sourceX', 'u32'], ['sourceY', 'u32'], ['width', 'u32'], ['height', 'u32'], ['depth', 'i32'], ['id', 'i32']]) equal(r[type](), item[key], label + '/tile/' + key);
        r.bool();
    }
    r.take(56);
});
equal(input.i32(), manifest.lastInstanceId, 'rooms/last-instance'); equal(input.i32(), manifest.lastTileId, 'rooms/last-tile');
input.version(800); equal(input.u32(), 0, 'includedFiles/count');
input.version(700); equal(input.u32(), 0, 'extensions/count');
input.version(800); input.chunk();
input.version(500); equal(input.u32(), manifest.library.length, 'library/count');
for (const key of manifest.library) source(input, key, 'library');
input.version(700); equal(input.u32(), manifest.roomOrder.length, 'rooms/order-count');
equal(Array.from({ length: manifest.roomOrder.length }, () => input.u32()), manifest.roomOrder, 'rooms/order');
console.log(JSON.stringify({ result: 'DECOMPILE_AUDIT_OK', referenceSha256: crypto.createHash('sha256').update(fs.readFileSync(reference)).digest('hex'), checks }));
