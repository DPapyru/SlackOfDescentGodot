import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

export function packageModToolchain(output, managed, root = path.resolve(import.meta.dirname, '..')) {
    const dotnet = process.env.DOTNET_ROOT ?? '/usr/lib/dotnet';
    const packs = path.join(dotnet, 'packs', 'Microsoft.NETCore.App.Ref');
    const versions = fs.readdirSync(packs).filter(x => x.startsWith('8.')).sort((a,b) => a.localeCompare(b, undefined, { numeric: true }));
    if (!versions.length) throw new Error('Missing net8 reference pack on packaging machine');
    const references = path.join(output, 'Tools', 'ModCompiler', 'References');
    fs.mkdirSync(references, { recursive: true });
    fs.cpSync(path.join(packs, versions.at(-1), 'ref', 'net8.0'), references, { recursive: true });
    for (const file of ['SlackOfDescent.dll', 'GodotSharp.dll']) fs.copyFileSync(path.join(managed, file), path.join(references, file));
    for (const file of ['Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll']) {
        if (!fs.existsSync(path.join(managed, file))) throw new Error('Missing bundled Roslyn compiler: ' + file);
    }
    fs.cpSync(path.join(root, 'Examples', 'Mods'), path.join(output, 'Examples', 'Mods'), { recursive: true });
    fs.writeFileSync(path.join(output, 'Tools', 'ModCompiler', 'toolchain.json'), JSON.stringify({ framework:'net8.0', referencePack:versions.at(-1), godot:'4.7.2', compiler:'Roslyn (in process)' }, null, 2));
    return references;
}
if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
    if (!process.argv[2] || !process.argv[3]) throw new Error('export-directory and managed-directory required');
    console.log(packageModToolchain(path.resolve(process.argv[2]), path.resolve(process.argv[3])));
}
