import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const graph = JSON.parse(readFileSync(fileURLToPath(new URL('./qte.motion.json', import.meta.url)), 'utf8'));
const SHOULDER = [1.31, -14.94];
const SEGMENT = 12;
const BONES = ['BODY_BOTTOM', 'BODY_TOP', 'HEAD', 'HAND1_ABOVE', 'HAND2_ABOVE', 'HAND3_ABOVE', 'HAND1_BELOW', 'HAND2_BELOW', 'HAND3_BELOW', 'FOOT1_ABOVE', 'FOOT2_ABOVE', 'FOOT3_ABOVE', 'FOOT1_BELOW', 'FOOT2_BELOW', 'FOOT3_BELOW'];
const LEGS = { FOOT1_ABOVE: [1.74, 9.85], FOOT2_ABOVE: [0, 22], FOOT3_ABOVE: [-1.31, 24.97], FOOT1_BELOW: [1.74, 9.85], FOOT2_BELOW: [0, 22], FOOT3_BELOW: [-1.31, 24.97] };
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('QTE_MOTION_CHECK FAILED: ' + name); assertions += 1; };
const place = (origin, angle) => [origin[0] + SEGMENT * Math.sin(angle * Math.PI / 180), origin[1] + SEGMENT * Math.cos(angle * Math.PI / 180)];
const near = (left, right, tolerance = 0.01) => Math.abs(left - right) <= tolerance;
const clip = graph.Clips[0];
const track = (bone) => clip.Tracks.find(item => item.Bone === bone);

expect(graph.FormatVersion === 1 && graph.Id === 'ropebonds.qte' && graph.Skeleton === 'Nana', 'graph header');
expect(graph.Clips.length === 1 && graph.Entry === 'qte' && graph.States.some(state => state.Id === 'qte' && state.Clip === 'qte'), 'entry state');
expect((graph.IkChains ?? []).length === 2, 'arm ik chains');
expect(clip.Id === 'qte' && clip.Duration === 40 && clip.Loop === true, 'clip header');
expect(clip.Tracks.length === BONES.length && BONES.every(name => clip.Tracks.some(item => item.Bone === name)), 'all bones tracked');
for (const item of clip.Tracks) {
    expect(item.Keys.length >= 2 && item.Keys.every((key, index) => Number.isFinite(key.X) && Number.isFinite(key.Y) && Number.isFinite(key.Angle)
        && key.Frame >= 0 && key.Frame <= clip.Duration && (index === 0 || key.Frame > item.Keys[index - 1].Frame)), 'keys valid ' + item.Bone);
    const first = item.Keys[0], last = item.Keys[item.Keys.length - 1];
    expect(near(first.X, last.X) && near(first.Y, last.Y) && near(first.Angle, last.Angle) && first.Picture === last.Picture, 'loop closes ' + item.Bone);
}
expect(track('HEAD').Keys.every(key => near(key.X, 0) && near(key.Y, -18)), 'head pivot');
expect(track('BODY_BOTTOM').Keys.every(key => near(key.Angle, 0)), 'pelvis planted');
expect(track('BODY_TOP').Keys.every(key => near(key.X, 0) && near(key.Y, 0)), 'waist pivot');
for (const [bone, [x, y]] of Object.entries(LEGS)) expect(track(bone).Keys.every(key => near(key.X, x) && near(key.Y, y)), 'leg pivot ' + bone);
const armFrames = track('HAND1_ABOVE').Keys.map(key => key.Frame);
expect(JSON.stringify(armFrames) === JSON.stringify(track('HAND1_BELOW').Keys.map(key => key.Frame)), 'both arms keyed together');
let gap = 0;
for (let i = 0; i < armFrames.length; i++) {
    const hands = {};
    for (const side of ['ABOVE', 'BELOW']) {
        const upper = track('HAND1_' + side).Keys[i], lower = track('HAND2_' + side).Keys[i], wrist = track('HAND3_' + side).Keys[i];
        expect(near(upper.X, SHOULDER[0]) && near(upper.Y, SHOULDER[1]), 'shoulder on rig ' + side);
        expect(near(lower.X, 0) && near(lower.Y, SEGMENT), 'upper arm pivot ' + side);
        expect(near(wrist.X, 0) && near(wrist.Y, SEGMENT), 'wrist pivot ' + side);
        expect(wrist.Angle === 0, 'wrist follows forearm ' + side);
        const elbow = place(SHOULDER, upper.Angle);
        const hand = place(elbow, upper.Angle + lower.Angle);
        expect(hand[0] > SHOULDER[0] + 3, 'wrists in front ' + side);
        expect(hand[1] > -2 && hand[1] < 10, 'wrists at waist ' + side);
        expect(elbow[0] >= 0 && elbow[1] > -10, 'elbow stays on the rig ' + side);
        hands[side] = hand;
    }
    gap = Math.hypot(hands.ABOVE[0] - hands.BELOW[0], hands.ABOVE[1] - hands.BELOW[1]);
    expect(gap < 4 && gap > 0.5, 'wrists bound together ' + gap.toFixed(2));
}
const upperAngles = track('HAND1_ABOVE').Keys.map(key => key.Angle);
expect(new Set(upperAngles.map(value => value.toFixed(2))).size >= 3, 'arms pull during the loop');
console.log('QTE_MOTION_CHECK PASS ' + assertions + ' keys=' + armFrames.length + ' gap=' + gap.toFixed(2));
