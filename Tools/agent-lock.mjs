import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const lockDir = path.join(root, '.agent-lock');
fs.mkdirSync(lockDir, { recursive: true });
const args = process.argv.slice(2);
const resource = args.shift() ?? 'build';
const command = args.join(' ');
if (!command) throw new Error('usage: node Tools/agent-lock.mjs <resource> <command...>');
const result = spawnSync('flock', ['--close', '-w', '3600', path.join(lockDir, resource + '.lock'), 'bash', '-lc', command], { cwd: root, stdio: 'inherit', env: process.env });
process.exit(result.status ?? 1);
