import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const prepared = new Set();
export function prepareDiagnostics(project) {
    project = path.resolve(project);
    if (prepared.has(project)) return;
    execFileSync('dotnet', ['build', path.join(project, 'SlackOfDescent.csproj'), '-p:IncludeDiagnostics=true', '-p:NuGetAudit=false'], { cwd: project, stdio: 'inherit', timeout: 180000 });
    execFileSync('dotnet', ['run', '--project', path.join(import.meta.dirname, '../Tests/Architecture/ArchitectureTests.csproj'), '-p:NuGetAudit=false', '--', path.join(project, '.godot/mono/temp/bin/Debug/SlackOfDescent.dll'), 'diagnostics'], { stdio: 'inherit', timeout: 60000 });
    prepared.add(project);
}
export function diagnosticTemporary(name) {
    return mkdtempSync(path.join(os.tmpdir(), 'ccgame-' + name + '-'));
}
export function readDiagnosticResult(file) {
    return JSON.parse(readFileSync(file, 'utf8'));
}
export function runDiagnostic(godot, args, { project, timeout = 120000, env = process.env, stdio = 'pipe' }) {
    prepareDiagnostics(project);
    return execFileSync(godot, args, { cwd: project, env, stdio, timeout, maxBuffer: 64 * 1024 * 1024 });
}
export function removeDiagnosticResult(file) {
    rmSync(file, { force: true });
}
