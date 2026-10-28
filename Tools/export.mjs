import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { auditPackage, writeInventory } from './package-audit.mjs';
import { packageModToolchain } from './package-mod-toolchain.mjs';

const root = path.resolve(import.meta.dirname, '..');
const godot = process.argv[2] ?? process.env.GODOT_BIN;
if (!godot) throw new Error('GODOT_BIN or executable argument required');
const output = path.resolve(process.argv[3] ?? path.join(root, '../ccgame-builds'));
const targets = [
    { name: 'Linux', executable: 'SlackOfDescent.x86_64', compiler: 'release/slack-extract' },
    { name: 'Windows', executable: 'SlackOfDescent.exe', compiler: 'x86_64-pc-windows-gnu/release/slack-extract.exe' },
];
execFileSync('dotnet', ['build', 'SlackOfDescent.csproj', '-c', 'ExportRelease', '-p:IncludeDiagnostics=false', '-p:NuGetAudit=false'], { cwd: root, stdio: 'inherit' });
fs.mkdirSync(output, { recursive: true });
for (const target of targets) {
    const compiler = path.join(root, 'Tools/Extract/target', target.compiler);
    if (!fs.existsSync(compiler)) throw new Error('Missing parser: ' + compiler);
    const directory = path.join(output, target.name);
    const staging = fs.mkdtempSync(path.join(output, '.' + target.name + '-'));
    const prepared = path.join(staging, target.name);
    try {
        fs.mkdirSync(path.join(prepared, 'Tools'), { recursive: true });
        execFileSync(godot, ['--headless', '--path', root, '--export-release', target.name, path.join(prepared, target.executable)], { cwd: root, stdio: 'inherit', env: { ...process.env, IncludeDiagnostics: 'false' } });
        const managed = path.join(prepared, 'data_SlackOfDescent_' + (target.name === 'Linux' ? 'linuxbsd_x86_64' : 'windows_x86_64'));
        const runtimeFiles = target.name === 'Linux' ? ['libhostfxr.so', 'libcoreclr.so'] : ['hostfxr.dll', 'coreclr.dll'];
        for (const file of ['SlackOfDescent.dll', 'SlackOfDescent.runtimeconfig.json', 'GodotSharp.dll', ...runtimeFiles]) {
            if (!fs.existsSync(path.join(managed, file))) throw new Error('Missing exported .NET file: ' + path.join(managed, file));
        }
        execFileSync('dotnet', ['run', '--project', path.join(root, 'Tests/Architecture/ArchitectureTests.csproj'), '-p:NuGetAudit=false', '--', path.join(managed, 'SlackOfDescent.dll'), 'production'], { cwd: root, stdio: 'inherit' });
        for (const file of [target.executable, 'SlackOfDescent.pck']) {
            if (!fs.existsSync(path.join(prepared, file))) throw new Error('Missing exported file: ' + file);
        }
        fs.cpSync(path.join(root, 'GameData'), path.join(prepared, 'GameData'), { recursive: true, filter: file => !file.endsWith('.import') && path.basename(file) !== 'Thumbs.db' });
        fs.copyFileSync(compiler, path.join(prepared, 'Tools', path.basename(compiler)));
        packageModToolchain(prepared, managed, root);
        for (const file of ['manifest.json', 'code.json', 'sources.json', 'media.json', 'particles.bin', 'assets/Cfg/KeyConfig', 'assets/Cfg/data', 'assets/Cfg/itemData']) {
            if (!fs.existsSync(path.join(prepared, 'GameData', file))) throw new Error('Missing exported game data: ' + file);
        }
        if (target.name === 'Linux') {
            fs.chmodSync(path.join(prepared, target.executable), 0o755);
            fs.chmodSync(path.join(prepared, 'Tools/slack-extract'), 0o755);
        }
        writeInventory(prepared, target.name);
        console.log(JSON.stringify(auditPackage(prepared, path.join(root, 'GameData'))));
        const archive = path.join(staging, target.name + '.zip');
        if (process.platform === 'win32') {
            const quote = value => "'" + value.replaceAll("'", "''") + "'";
            execFileSync('powershell.exe', ['-NoProfile', '-Command', 'Compress-Archive -LiteralPath ' + quote(prepared) + ' -DestinationPath ' + quote(archive)], { stdio: 'inherit' });
        } else execFileSync('zip', ['-q', '-r', archive, target.name], { cwd: staging, stdio: 'inherit' });
        const previous = path.join(staging, 'previous');
        const finalArchive = path.join(output, target.name + '.zip');
        const previousArchive = path.join(staging, 'previous.zip');
        if (fs.existsSync(directory)) fs.renameSync(directory, previous);
        try {
            if (fs.existsSync(finalArchive)) fs.renameSync(finalArchive, previousArchive);
            fs.renameSync(archive, finalArchive);
            fs.renameSync(prepared, directory);
        }
        catch (error) {
            if (fs.existsSync(finalArchive) && !fs.existsSync(archive)) fs.renameSync(finalArchive, archive);
            if (fs.existsSync(previousArchive)) fs.renameSync(previousArchive, finalArchive);
            if (fs.existsSync(previous)) fs.renameSync(previous, directory);
            throw error;
        }
        console.log('EXPORT_READY ' + directory);
    } finally {
        fs.rmSync(staging, { recursive: true, force: true });
    }
}
