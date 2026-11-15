import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const TILE = 32, FLOOR_Y = 400, PIT_W = 320;
const document = JSON.parse(readFileSync(fileURLToPath(new URL('./demo.map.json', import.meta.url)), 'utf8'));
const room = document.Room;
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('DEMO_MAP_CHECK FAILED: ' + name); assertions += 1; };

expect(room.width === 2400 && room.height === 1280, 'room size');
expect(room.code === 'rooms/11/create' && document.CreationCode[room.code] != null, 'Playground room code restored');
expect(JSON.stringify(document.CreationCode['rooms/11/create']).includes('EASY_QS'), 'Playground create keeps EASY_QS=0 behaviour');

const blocks = new Map(), quicksand = new Map(), creatures = [];
const ids = new Set();
for (const instance of room.instances) {
    expect(Number.isInteger(instance.id) && !ids.has(instance.id), 'unique instance id ' + instance.id);
    ids.add(instance.id);
    expect(typeof instance.code === 'string', 'instance code string');
    if (instance.code && document.CreationCode[instance.code] == null) expect(false, 'creation code present for ' + instance.code);
    if (instance.entityType) { creatures.push(instance); continue; }
    expect(document.ObjectNames[String(instance.object)] != null, 'object name for ' + instance.object);
    if (instance.object === 191) blocks.set(instance.x + ',' + instance.y, instance);
    if (instance.object === 357) quicksand.set(instance.x + ',' + instance.y, instance);
}
expect(creatures.length === 3 && document.Dependencies.includes('ropebonds.creature'), 'three rope creature entities');
expect(room.instances.filter(instance => instance.object === 297).length === 1, 'objFogController instance');
expect(room.instances.filter(instance => instance.object === 292).length === 1, 'objDark3 instance');

const positions = (x, y) => blocks.has(x + ',' + y);
const gaps = [];
let run = null;
for (let x = 0; x <= room.width - TILE; x += TILE) {
    if (positions(x, FLOOR_Y)) { if (run) { gaps.push(run); run = null; } }
    else (run ??= { start: x, end: x + TILE }).end = x + TILE;
}
expect(gaps.length === 4, 'three pits plus quicksand break the ground line');
const pits = gaps.filter(gap => !quicksand.has(gap.start + ',' + FLOOR_Y));
expect(pits.length === 3 && pits.every(gap => gap.end - gap.start === PIT_W), 'three pits are 320 wide');
const pool = gaps.find(gap => quicksand.has(gap.start + ',' + FLOOR_Y));
expect(pool && pool.end - pool.start === 320, 'quicksand spans its ground gap');

for (const gap of pits) {
    for (let x = gap.start; x < gap.end; x += TILE) {
        expect(!positions(x, FLOOR_Y), 'pit mouth open at ' + x);
        expect(!positions(x, FLOOR_Y + TILE), 'pit wall interior clear at ' + x);
        expect(positions(x, FLOOR_Y + 2 * TILE), 'pit floor continuous at ' + x);
    }
    expect(positions(gap.start - TILE, FLOOR_Y + TILE) && positions(gap.end, FLOOR_Y + TILE), 'pit walls at both sides');
    expect(positions(gap.start - TILE, FLOOR_Y) && positions(gap.end, FLOOR_Y), 'ground meets pit mouth');
    expect(creatures.filter(creature => creature.x >= gap.start && creature.x < gap.end && creature.y === FLOOR_Y + 2 * TILE - 1).length === 1, 'creature rests on pit floor ' + gap.start);
}
for (let x = pool.start - TILE; x < pool.end + TILE; x += TILE) {
    const ground = x === pool.start - TILE || x === pool.end;
    expect(ground ? positions(x, FLOOR_Y) : quicksand.has(x + ',' + FLOOR_Y), 'quicksand surface flush with ground at ' + x);
}
expect(quicksand.size === (pool.end - pool.start) / TILE, 'quicksand columns contiguous');
for (let x = pool.start; x < pool.end; x += TILE) expect(positions(x, FLOOR_Y + 256), 'quicksand floor below surface at ' + x);

for (let x = 0; x < room.width; x += TILE) {
    expect(positions(x, 0) && positions(x, room.height - TILE), 'closed top and bottom boundary at ' + x);
}
for (let y = TILE; y < room.height - TILE; y += TILE) {
    expect(positions(0, y) && positions(room.width - TILE, y), 'closed side boundary at ' + y);
}
console.log('DEMO_MAP_CHECK PASS assertions=' + assertions + ' instances=' + room.instances.length + ' pits=' + pits.map(gap => gap.start + '..' + gap.end).join(',') + ' quicksand=' + pool.start + '..' + pool.end);
