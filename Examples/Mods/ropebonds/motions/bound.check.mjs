import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const file = fileURLToPath(new URL('./bound.motion.json', import.meta.url));
const graph = JSON.parse(readFileSync(file, 'utf8'));
const SHOULDER = [1.31, -14.94];
const SEGMENT = 12;
const BONES = new Set(['BODY_BOTTOM', 'BODY_TOP', 'HEAD', 'HAND1_ABOVE', 'HAND2_ABOVE', 'HAND3_ABOVE', 'HAND1_BELOW', 'HAND2_BELOW', 'HAND3_BELOW', 'FOOT1_ABOVE', 'FOOT2_ABOVE', 'FOOT3_ABOVE', 'FOOT1_BELOW', 'FOOT2_BELOW', 'FOOT3_BELOW']);
const TARGETS = { ABOVE: [6, -8], BELOW: [6, -10.5] };
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('BOUND_MOTION_CHECK FAILED: ' + name); assertions += 1; };
const rad = value => value * Math.PI / 180;
const deg = value => value * 180 / Math.PI;
const place = (origin, angle) => [origin[0] + SEGMENT * Math.sin(rad(angle)), origin[1] + SEGMENT * Math.cos(rad(angle))];

const solve = (target, elbowBack) => {
    const vx = target[0] - SHOULDER[0], vy = target[1] - SHOULDER[1];
    const distance = Math.hypot(vx, vy);
    expect(distance > 0.01 && distance < 2 * SEGMENT - 0.01, 'target reachable ' + target);
    const phi = Math.atan2(vx, vy), delta = Math.acos(distance / (2 * SEGMENT));
    const a1 = deg(phi + (elbowBack ? -delta : delta)), a2 = deg(phi + (elbowBack ? delta : -delta));
    return { angle1: a1, angle2: a2 - a1 };
};

const hands = {};
for (const side of ['ABOVE', 'BELOW']) {
    const track1 = graph.Clips[0].Tracks.find(track => track.Bone === 'HAND1_' + side);
    const track2 = graph.Clips[0].Tracks.find(track => track.Bone === 'HAND2_' + side);
    expect(track1 && track2, 'arm tracks present ' + side);
    const elbow = place(SHOULDER, track1.Keys[0].Angle);
    const hand = place(elbow, track1.Keys[0].Angle + track2.Keys[0].Angle);
    hands[side] = hand;
    const solved = solve(TARGETS[side], true);
    expect(Math.abs(track1.Keys[0].Angle - solved.angle1) < 0.05, 'upper arm matches target ' + side);
    expect(Math.abs(track2.Keys[0].Angle - solved.angle2) < 0.05, 'forearm matches target ' + side);
    expect(Math.abs(hand[0] - TARGETS[side][0]) < 0.05 && Math.abs(hand[1] - TARGETS[side][1]) < 0.05, 'hand lands on target ' + side);
}

expect(Math.hypot(hands.ABOVE[0] - hands.BELOW[0], hands.ABOVE[1] - hands.BELOW[1]) < 3, 'wrists stay together');
expect(hands.ABOVE[0] > SHOULDER[0] + 3, 'wrists sit in front of the shoulders');
expect(graph.FormatVersion === 1 && graph.Id === 'ropebonds.bound' && graph.Skeleton === 'Nana', 'graph header');
expect(graph.Clips.length > 0 && graph.States.some(state => state.Id === graph.Entry && state.Clip === graph.Clips[0].Id), 'entry state references the clip');
const clip = graph.Clips[0];
expect(clip.Duration > 0 && clip.Tracks.every(track =>
    BONES.has(track.Bone) && track.Keys.length > 0 &&
    track.Keys.every((key, index) => key.Frame >= 0 && key.Frame <= clip.Duration && (index === 0 || key.Frame > track.Keys[index - 1].Frame))), 'clip keys valid');

if (process.argv.includes('--write')) {
    for (const side of ['ABOVE', 'BELOW']) {
        const solved = solve(TARGETS[side], true);
        const track1 = clip.Tracks.find(track => track.Bone === 'HAND1_' + side);
        const track2 = clip.Tracks.find(track => track.Bone === 'HAND2_' + side);
        for (const key of track1.Keys) key.Angle = Number(solved.angle1.toFixed(1));
        for (const key of track2.Keys) key.Angle = Number(solved.angle2.toFixed(1));
    }
    writeFileSync(file, JSON.stringify(graph, null, 2) + '\n');
    console.log('BOUND_MOTION_WRITTEN');
}

console.log('BOUND_MOTION_CHECK PASS ' + assertions + ' hands=' + JSON.stringify(hands));
