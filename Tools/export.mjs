import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const godot = process.argv[2] ?? process.env.GODOT_BIN;
if (!godot) throw new Error('GODOT_BIN or executable argument required');
const output = path.resolve(process.argv[3] ?? path.join(root, '../ccgame-builds'));
const targets = [
    { name: 'Linux', executable: 'SlackOfDescent.x86_64', compiler: 'release/slack-extract' },
    { name: 'Windows', executable: 'SlackOfDescent.exe', compiler: 'x86_64-pc-windows-gnu/release/slack-extract.exe' },
];
for (const target of targets) {
    const compiler = path.join(root, 'Tools/Extract/target', target.compiler);
    if (!fs.existsSync(compiler)) throw new Error('Missing parser: ' + compiler);
    const directory = path.join(output, target.name);
    fs.mkdirSync(path.join(directory, 'Tools'), { recursive: true });
    execFileSync(godot, ['--headless', '--path', root, '--export-release', target.name, path.join(directory, target.executable)], { cwd: root, stdio: 'inherit' });
    const managed = path.join(directory, 'data_SlackOfDescent_' + (target.name === 'Linux' ? 'linuxbsd_x86_64' : 'windows_x86_64'));
    if (!fs.existsSync(path.join(managed, 'SlackOfDescent.dll'))) throw new Error('Missing exported .NET assembly: ' + managed);
    fs.cpSync(path.join(root, 'GameData'), path.join(directory, 'GameData'), { recursive: true, filter: file => !file.endsWith('.import') });
    fs.copyFileSync(compiler, path.join(directory, 'Tools', path.basename(compiler)));
    if (target.name === 'Linux') {
        fs.chmodSync(path.join(directory, target.executable), 0o755);
        fs.chmodSync(path.join(directory, 'Tools/slack-extract'), 0o755);
    }
    console.log('EXPORT_READY ' + directory);
}
