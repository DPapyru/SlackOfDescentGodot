import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const TILE = 32, GROUND_Y = 400, PIT_W = 320, PIT_DEPTH = 64;
const document = JSON.parse(readFileSync(fileURLToPath(new URL('./demo.map.json', import.meta.url)), 'utf8'));
const room = document.Room;
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('DEMO_MAP_CHECK FAILED: ' + name); assertions += 1; };

expect(room.width === 2400 && room.height === 1280, 'room size');
expect(room.code === '', 'room code cleared');

const blocks = new Map(), quicksand = new Map(), creatures = [], spawn = [];
const ids = new Set();
for (const instance of room.instances) {
    expect(Number.isInteger(instance.id) && !ids.has(instance.id), 'unique instance id ' + instance.id);
    ids.add(instance.id);
    expect(typeof instance.code === 'string', 'instance code string');
    if (instance.code && document.CreationCode[instance.code] == null) expect(false, 'creation code present for ' + instance.code);
    if (instance.entityType) { creatures.push(instance); continue; }
    expect(document.ObjectNames[String(instance.object)] != null, 'object name for ' + instance.object);
    if (instance.object === 191) blocks.set(instance.x + ',' + instance.y, instance);
    if (instance.object === 357) { expect(instance.y === GROUND_Y, 'quicksand surface on ground line'); quicksand.set(instance.x + ',' + instance.y, instance); }
    if (instance.object === 30) spawn.push(instance);
}
expect(!room.instances.some(instance => instance.object === 292 || instance.object === 297), 'fog/dark instances removed');
expect(spawn.length === 1 && spawn[0].y + 64 === GROUND_Y, 'player spawn stands on the ground line');
expect(creatures.length === 3 && document.Dependencies.includes('ropebonds.creature'), 'three rope creature entities');
const legBinder = creatures.filter(creature => creature.properties?.LegsOnly === 1);
expect(legBinder.length === 1, 'one creature binds the leg layer only');
expect(legBinder[0].y === GROUND_Y + PIT_DEPTH - 32, 'leg layer creature spawns above a pit floor');
const byX = x => creatures.find(creature => creature.x === x);
const winner = byX(470), loser = byX(1110);
expect(winner && winner.properties?.PreBind === 1 && winner.properties?.AutoQte === 1, 'pit one runs the leg qte success branch');
expect(loser && loser.properties?.PreBind === 1 && loser.properties?.AutoQte === 2, 'pit two runs the leg qte fail branch');
expect(legBinder[0].properties?.PreBind === undefined && legBinder[0].properties?.AutoQte === undefined, 'the leg layer debug creature runs no qte');

const positions = (x, y) => blocks.has(x + ',' + y);
const gaps = [];
let run = null;
for (let x = 0; x <= room.width - TILE; x += TILE) {
    if (positions(x, GROUND_Y)) { if (run) { gaps.push(run); run = null; } }
    else (run ??= { start: x, end: x + TILE }).end = x + TILE;
}
expect(gaps.length === 4, 'three pits plus quicksand break the ground line');
const pits = gaps.filter(gap => !quicksand.has(gap.start + ',' + GROUND_Y));
expect(pits.length === 3 && pits.every(gap => gap.end - gap.start === PIT_W), 'three pits are 320 wide');
const pool = gaps.find(gap => quicksand.has(gap.start + ',' + GROUND_Y));
expect(pool && pool.end - pool.start === 320, 'quicksand spans its ground gap');

for (const gap of pits) {
    expect(gap.start - TILE >= 0 && gap.end + TILE <= room.width, 'pit mouth inside ground line');
    for (let x = gap.start; x < gap.end; x += TILE) {
        expect(!positions(x, GROUND_Y), 'pit mouth open at ' + x);
        expect(!positions(x, GROUND_Y + TILE), 'pit interior clear at ' + x);
        expect(positions(x, GROUND_Y + PIT_DEPTH), 'pit floor continuous at ' + x);
    }
    expect(positions(gap.start - TILE, GROUND_Y + TILE) && positions(gap.end, GROUND_Y + TILE), 'pit walls at both sides');
    expect(positions(gap.start - TILE, GROUND_Y) && positions(gap.end, GROUND_Y), 'ground meets pit mouth');
    expect(creatures.filter(creature => creature.x >= gap.start && creature.x < gap.end && creature.y === GROUND_Y + PIT_DEPTH - 32).length === 1, 'creature spawns above pit floor ' + gap.start);
}
for (let x = pool.start - TILE; x < pool.end + TILE; x += TILE) {
    const ground = x === pool.start - TILE || x === pool.end;
    expect(ground ? positions(x, GROUND_Y) : quicksand.has(x + ',' + GROUND_Y), 'quicksand surface flush with ground at ' + x);
}
expect(quicksand.size === (pool.end - pool.start) / TILE, 'quicksand columns contiguous');
for (let x = pool.start; x < pool.end; x += TILE) expect(positions(x, GROUND_Y + 256), 'quicksand floor below surface at ' + x);

for (let x = 0; x < room.width; x += TILE) {
    expect(positions(x, 0) && positions(x, room.height - TILE), 'closed top and bottom boundary at ' + x);
}
for (let y = TILE; y < room.height - TILE; y += TILE) {
    expect(positions(0, y) && positions(room.width - TILE, y), 'closed side boundary at ' + y);
}
console.log('DEMO_MAP_CHECK PASS assertions=' + assertions + ' instances=' + room.instances.length + ' ground=' + GROUND_Y + ' pitFloor=' + (GROUND_Y + PIT_DEPTH) + ' quicksand=' + pool.start + '..' + pool.end + '@' + GROUND_Y);
