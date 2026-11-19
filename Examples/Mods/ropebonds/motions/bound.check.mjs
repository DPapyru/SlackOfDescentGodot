import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const graph = JSON.parse(readFileSync(fileURLToPath(new URL('./bound.motion.json', import.meta.url)), 'utf8'));
const SHOULDER = [1.31, -14.94];
const SEGMENT = 12;
const BONES = new Set(['BODY_BOTTOM', 'BODY_TOP', 'HEAD', 'HAND1_ABOVE', 'HAND2_ABOVE', 'HAND3_ABOVE', 'HAND1_BELOW', 'HAND2_BELOW', 'HAND3_BELOW', 'FOOT1_ABOVE', 'FOOT2_ABOVE', 'FOOT3_ABOVE', 'FOOT1_BELOW', 'FOOT2_BELOW', 'FOOT3_BELOW']);
const WRISTS = { ABOVE: 8, BELOW: 1 };
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('BOUND_MOTION_CHECK FAILED: ' + name); assertions += 1; };
const place = (origin, angle) => [origin[0] + SEGMENT * Math.sin(angle * Math.PI / 180), origin[1] + SEGMENT * Math.cos(angle * Math.PI / 180)];

expect(graph.FormatVersion === 1 && graph.Id === 'ropebonds.bound' && graph.Skeleton === 'Nana', 'graph header');
expect(graph.Clips.length > 0 && graph.States.some(state => state.Id === graph.Entry && state.Clip === graph.Clips[0].Id), 'entry state references the clip');
const clip = graph.Clips[0];
expect(clip.Duration > 0 && clip.Tracks.length === 6, 'clip tracks');
expect(clip.Tracks.every(track => BONES.has(track.Bone) && track.Keys.length > 0 &&
    track.Keys.every((key, index) => key.Frame >= 0 && key.Frame <= clip.Duration && (index === 0 || key.Frame > track.Keys[index - 1].Frame))), 'clip keys valid');
expect(clip.Tracks.every(track => track.Bone.startsWith('HAND')), 'arm tracks only');

const hands = {};
for (const side of ['ABOVE', 'BELOW']) {
    const upper = clip.Tracks.find(track => track.Bone === 'HAND1_' + side).Keys[0];
    const lower = clip.Tracks.find(track => track.Bone === 'HAND2_' + side).Keys[0];
    const wrist = clip.Tracks.find(track => track.Bone === 'HAND3_' + side).Keys[0];
    expect(upper.X === SHOULDER[0] && upper.Y === SHOULDER[1], 'shoulder on the rig ' + side);
    expect(Math.abs(lower.X) < 0.01 && lower.Y === SEGMENT, 'upper arm keeps its pivot ' + side);
    expect(Math.abs(wrist.X) < 0.01 && wrist.Y === SEGMENT, 'wrist keeps its pivot ' + side);
    expect(wrist.Angle === 0, 'wrist follows the forearm ' + side);
    expect(wrist.Picture === WRISTS[side], 'wrist picture ' + side);
    const elbow = place(SHOULDER, upper.Angle);
    const hand = place(elbow, upper.Angle + lower.Angle);
    expect(Math.abs(Math.hypot(elbow[0] - SHOULDER[0], elbow[1] - SHOULDER[1]) - SEGMENT) < 0.01, 'upper arm length ' + side);
    hands[side] = hand;
}

const gap = Math.hypot(hands.ABOVE[0] - hands.BELOW[0], hands.ABOVE[1] - hands.BELOW[1]);
expect(gap < 4, 'wrists together ' + gap.toFixed(2));
expect(hands.ABOVE[0] < SHOULDER[0] - 2 && hands.BELOW[0] < SHOULDER[0] - 2, 'wrists behind the shoulders');
expect(hands.ABOVE[1] > SHOULDER[1] + 2 && hands.BELOW[1] > SHOULDER[1] + 2, 'wrists below the shoulders');

console.log('BOUND_MOTION_CHECK PASS ' + assertions + ' gap=' + gap.toFixed(2) + ' hands=' + JSON.stringify({ ABOVE: hands.ABOVE.map(v => Number(v.toFixed(2))), BELOW: hands.BELOW.map(v => Number(v.toFixed(2))) }));
