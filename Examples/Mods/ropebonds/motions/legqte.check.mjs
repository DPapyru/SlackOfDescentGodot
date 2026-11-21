import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const graph = JSON.parse(readFileSync(fileURLToPath(new URL('./legqte.motion.json', import.meta.url)), 'utf8'));
const PIVOTS = { FOOT1: [1.74, 9.85], FOOT2: [0, 22], FOOT3: [-1.31, 24.97] };
const FEET = ['FOOT1_ABOVE', 'FOOT2_ABOVE', 'FOOT3_ABOVE', 'FOOT1_BELOW', 'FOOT2_BELOW', 'FOOT3_BELOW'];
let assertions = 0;
const expect = (condition, name) => { if (!condition) throw new Error('LEGQTE_MOTION_CHECK FAILED: ' + name); assertions += 1; };
const near = (left, right, tolerance = 1e-9) => Math.abs(left - right) <= tolerance;
const pivot = bone => PIVOTS[bone.slice(0, 5)];
const track = bone => graph.Clips[0].Tracks.find(item => item.Bone === bone);

expect(graph.FormatVersion === 1 && graph.Id === 'ropebonds.legqte' && graph.Skeleton === 'Nana', 'graph header');
expect(graph.Clips.length === 1 && graph.States.some(state => state.Id === graph.Entry && state.Clip === graph.Clips[0].Id), 'entry state references the clip');
const clip = graph.Clips[0];
expect(clip.Id === 'legqte' && clip.Duration === 40 && clip.Loop === true, 'clip header');
expect(clip.Tracks.length === FEET.length && FEET.every(bone => track(bone) !== undefined), 'foot tracks only');
for (const bone of FEET) {
    const keys = track(bone).Keys;
    const [x, y] = pivot(bone);
    expect(keys.length >= 2 && keys.every((key, index) => key.Frame >= 0 && key.Frame <= clip.Duration && (index === 0 || key.Frame > keys[index - 1].Frame)), 'keys ordered ' + bone);
    expect(keys.every(key => near(key.X, x) && near(key.Y, y)), 'pivot stays on the rig ' + bone);
    expect(near(keys[0].Angle, keys[keys.length - 1].Angle) && near(keys[0].X, keys[keys.length - 1].X) && near(keys[0].Y, keys[keys.length - 1].Y), 'loop closes ' + bone);
}
for (const side of ['ABOVE', 'BELOW']) {
    const thigh = track('FOOT1_' + side).Keys.map(key => key.Angle.toFixed(3));
    const shin = track('FOOT2_' + side).Keys.map(key => key.Angle.toFixed(3));
    expect(new Set(thigh).size >= 2 && new Set(shin).size >= 2, 'legs struggle ' + side);
    expect(track('FOOT3_' + side).Keys.every(key => key.Angle === 0), 'ankle follows the shin ' + side);
}
expect((graph.IkChains ?? []).length === 2 && graph.IkChains.every(chain => chain.Root.startsWith('FOOT1') && chain.Mid.startsWith('FOOT2') && chain.End.startsWith('FOOT3')), 'foot ik chains');
const closed = track('FOOT1_ABOVE').Keys[0];
expect(near(closed.Angle, -28.582477673968203), 'clip starts on the bound leg pose');
console.log('LEGQTE_MOTION_CHECK PASS ' + assertions + ' keys=' + track('FOOT1_ABOVE').Keys.length);
