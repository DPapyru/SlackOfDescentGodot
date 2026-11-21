import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const graph = JSON.parse(readFileSync(fileURLToPath(new URL('./legs.motion.json', import.meta.url)), 'utf8'));
const HIP = [1.74, 9.85];
const THIGH = 22;
const ANKLE = [-1.31, 24.97];
const SHIN = Math.hypot(ANKLE[0], ANKLE[1]);
const CONTACT = [HIP[0], HIP[1] + 42];
const FEET = ['FOOT1_ABOVE', 'FOOT2_ABOVE', 'FOOT3_ABOVE', 'FOOT1_BELOW', 'FOOT2_BELOW', 'FOOT3_BELOW'];
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('LEGS_MOTION_CHECK FAILED: ' + name); assertions += 1; };
const near = (left, right, tolerance = 1e-9) => Math.abs(left - right) <= tolerance;
const shift = (origin, angle, x, y) => [origin[0] + x * Math.cos(angle * Math.PI / 180) + y * Math.sin(angle * Math.PI / 180), origin[1] - x * Math.sin(angle * Math.PI / 180) + y * Math.cos(angle * Math.PI / 180)];
const degrees = Math.PI / 180;

expect(graph.FormatVersion === 1 && graph.Id === 'ropebonds.legs' && graph.Skeleton === 'Nana', 'graph header');
expect(graph.Clips.length === 1 && graph.States.some(state => state.Id === graph.Entry && state.Clip === graph.Clips[0].Id), 'entry state references the clip');
const clip = graph.Clips[0];
const track = bone => clip.Tracks.find(item => item.Bone === bone);
const key = bone => track(bone).Keys[0];
expect(clip.Id === 'legs' && clip.Duration === 30 && clip.Loop === true, 'clip header');
expect(clip.Tracks.length === FEET.length && FEET.every(bone => track(bone) !== undefined), 'leg tracks only');
expect(clip.Tracks.every(item => item.Keys.length === 2 && item.Keys.every((entry, index) => entry.Frame >= 0 && entry.Frame <= clip.Duration && (index === 0 || entry.Frame > item.Keys[index - 1].Frame))), 'two ordered keys per track');
expect(clip.Tracks.every(item => near(item.Keys[0].X, item.Keys[1].X) && near(item.Keys[0].Y, item.Keys[1].Y) && near(item.Keys[0].Angle, item.Keys[1].Angle) && item.Keys[0].Picture === item.Keys[1].Picture), 'the pose holds over the loop');
expect((graph.IkChains ?? []).length === 2 && graph.IkChains.every(chain => chain.Enabled && chain.Root.startsWith('FOOT1') && chain.Mid.startsWith('FOOT2') && chain.End.startsWith('FOOT3')), 'foot ik chains');

for (const side of ['ABOVE', 'BELOW']) {
    expect(near(key('FOOT1_' + side).X, HIP[0]) && near(key('FOOT1_' + side).Y, HIP[1]), 'hip pivot on the rig ' + side);
    expect(near(key('FOOT2_' + side).X, 0) && near(key('FOOT2_' + side).Y, THIGH), 'thigh keeps its pivot ' + side);
    expect(near(key('FOOT3_' + side).X, ANKLE[0]) && near(key('FOOT3_' + side).Y, ANKLE[1]), 'ankle keeps its pivot ' + side);
    expect(key('FOOT3_' + side).Angle === 0, 'ankle follows the shin ' + side);
}

const knees = {}, ankles = {};
for (const side of ['ABOVE', 'BELOW']) {
    const knee = shift([key('FOOT1_' + side).X, key('FOOT1_' + side).Y], key('FOOT1_' + side).Angle, 0, THIGH);
    knees[side] = knee;
    ankles[side] = shift(knee, key('FOOT1_' + side).Angle + key('FOOT2_' + side).Angle, ANKLE[0], ANKLE[1]);
}
const gap = Math.hypot(ankles.ABOVE[0] - ankles.BELOW[0], ankles.ABOVE[1] - ankles.BELOW[1]);
expect(gap < 0.01, 'ankles touch ' + gap.toFixed(4));
expect(near((ankles.ABOVE[0] + ankles.BELOW[0]) / 2, CONTACT[0], 0.01) && near((ankles.ABOVE[1] + ankles.BELOW[1]) / 2, CONTACT[1], 0.01), 'ankles meet on the contact point');
expect(knees.ABOVE[0] < HIP[0] - 2 && knees.BELOW[0] > HIP[0] + 2, 'thighs splay outwards');
expect(key('FOOT1_ABOVE').Angle * key('FOOT1_BELOW').Angle < 0, 'thighs carry opposite offsets');
expect(key('FOOT2_ABOVE').Angle * key('FOOT2_BELOW').Angle < 0, 'shins carry opposite offsets');
expect(key('FOOT1_ABOVE').Angle + key('FOOT2_ABOVE').Angle !== key('FOOT1_BELOW').Angle + key('FOOT2_BELOW').Angle, 'shins point at different angles');

const distance = Math.hypot(CONTACT[0] - HIP[0], CONTACT[1] - HIP[1]);
expect(distance > Math.abs(THIGH - SHIN) + 0.5 && distance < THIGH + SHIN - 0.5, 'contact stays inside the reach');
const root = (THIGH * THIGH + distance * distance - SHIN * SHIN) / (2 * distance);
const height = Math.sqrt(THIGH * THIGH - root * root);
for (const side of ['ABOVE', 'BELOW']) {
    const sign = side === 'ABOVE' ? -1 : 1;
    const knee = [HIP[0] + sign * height, HIP[1] + root];
    const first = Math.atan2(knee[0] - HIP[0], knee[1] - HIP[1]) / degrees;
    const second = Math.atan2(CONTACT[0] - knee[0], CONTACT[1] - knee[1]) / degrees - Math.atan2(ANKLE[0], ANKLE[1]) / degrees - first;
    expect(near(first, key('FOOT1_' + side).Angle), 'thigh is the ik solution ' + side);
    expect(near(second, key('FOOT2_' + side).Angle), 'shin is the ik solution ' + side);
}
console.log('LEGS_MOTION_CHECK PASS ' + assertions + ' gap=' + gap.toFixed(4) + ' contact=' + JSON.stringify(CONTACT) + ' ankles=' + JSON.stringify(ankles));
