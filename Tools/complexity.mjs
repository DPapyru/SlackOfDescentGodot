import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const root = path.resolve(import.meta.dirname, '..');
const baselinePath = path.join(root, 'Tools/complexity-baseline.json');
const baselineLimit = 8;
const keywords = new Set(['if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'fixed', 'when', 'nameof', 'typeof', 'sizeof', 'default', 'checked', 'unchecked', 'this', 'base', 'return', 'throw', 'new', 'delegate', 'stackalloc', 'in', 'is', 'as', 'out', 'ref', 'await', 'yield', 'goto', 'case', 'do', 'else', 'try', 'finally', 'var']);

const isIdent = character => character !== undefined && /[A-Za-z0-9_]/.test(character);

export function threshold(files, lines) {
    if (files <= 2 && lines <= 60) return 8;
    if (files <= 6 && lines <= 400) return 20;
    return 25;
}

export function maskSource(text) {
    const chars = text.split('');
    const blank = index => { if (chars[index] !== '\n') chars[index] = ' '; };
    let i = 0;
    while (i < text.length) {
        const end = literalEnd(text, i);
        if (end > i) {
            for (let k = i; k < end; k++) blank(k);
            i = end;
            continue;
        }
        if (text[i] === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') blank(i++);
            continue;
        }
        if (text[i] === '/' && text[i + 1] === '*') {
            blank(i++);
            blank(i++);
            while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) blank(i++);
            blank(i++);
            blank(i++);
            continue;
        }
        i++;
    }
    return chars.join('');
}

function literalEnd(text, index) {
    if (text[index] === '@' && (text[index + 1] === '"' || (text[index + 1] === '$' && text[index + 2] === '"'))) return verbatimEnd(text, text[index + 1] === '"' ? index + 1 : index + 2);
    let cursor = index;
    while (text[cursor] === '$') cursor++;
    if (text[cursor] === '@' && text[cursor + 1] === '"') return verbatimEnd(text, cursor + 1);
    const quote = cursor;
    if (text[quote] === '"') {
        let count = 0;
        while (text[quote + count] === '"') count++;
        if (count >= 3) {
            let i = quote + count;
            while (i < text.length) {
                let run = 0;
                while (text[i + run] === '"') run++;
                if (run >= count) return i + run;
                i += Math.max(1, run);
            }
            return text.length;
        }
        let i = quote + 1;
        while (i < text.length) {
            if (text[i] === '\\') { i += 2; continue; }
            if (text[i] === '"') return i + 1;
            i++;
        }
        return text.length;
    }
    if (text[index] === "'") {
        let i = index + 1;
        while (i < text.length) {
            if (text[i] === '\\') { i += 2; continue; }
            if (text[i] === "'") return i + 1;
            i++;
        }
        return text.length;
    }
    return -1;
}

function verbatimEnd(text, quoteIndex) {
    let i = quoteIndex + 1;
    while (i < text.length) {
        if (text[i] === '"' && text[i + 1] === '"') { i += 2; continue; }
        if (text[i] === '"') return i + 1;
        i++;
    }
    return text.length;
}

function skipSpace(text, index) {
    let i = index;
    while (i < text.length && /\s/.test(text[i])) i++;
    return i;
}

function matchPair(text, index, open, close) {
    let depth = 0;
    for (let i = index; i < text.length; i++) {
        if (text[i] === open) depth++;
        else if (text[i] === close) {
            depth--;
            if (depth === 0) return i;
        }
    }
    return -1;
}

function wordIs(text, index, word) {
    return text.startsWith(word, index) && !isIdent(text[index + word.length]) && (index === 0 || !isIdent(text[index - 1]));
}

function genericOpen(text, closeIndex) {
    let depth = 0;
    for (let i = closeIndex; i >= 0; i--) {
        if (text[i] === '>') depth++;
        else if (text[i] === '<') {
            depth--;
            if (depth === 0) return i;
        } else if (';{}()'.includes(text[i])) return -1;
    }
    return -1;
}

function lineIndex(text) {
    const starts = [0];
    for (let i = 0; i < text.length; i++) if (text[i] === '\n') starts.push(i + 1);
    return offset => {
        let low = 0;
        let high = starts.length - 1;
        while (low < high) {
            const middle = (low + high + 1) >> 1;
            if (starts[middle] <= offset) low = middle;
            else high = middle - 1;
        }
        return low + 1;
    };
}

function complexity(text) {
    const branches = text.match(/\b(?:if|for|foreach|while|case|catch)\b/g) ?? [];
    const logical = text.match(/&&|\|\||\?\?/g) ?? [];
    const ternary = text.match(/(?<=\s)\?(?!\?|\.|\[|>)/g) ?? [];
    return branches.length + logical.length + ternary.length;
}

function memberHead(text, parenIndex) {
    let cursor = parenIndex - 1;
    while (cursor >= 0 && /\s/.test(text[cursor])) cursor--;
    if (cursor < 0) return null;
    let nameStart = -1;
    let name = null;
    if (isIdent(text[cursor])) {
        nameStart = cursor;
        while (nameStart > 0 && isIdent(text[nameStart - 1])) nameStart--;
        name = text.slice(nameStart, cursor + 1);
    } else if (text[cursor] === '>') {
        const open = genericOpen(text, cursor);
        if (open > 0 && isIdent(text[open - 1])) {
            nameStart = open - 1;
            while (nameStart > 0 && isIdent(text[nameStart - 1])) nameStart--;
            name = text.slice(nameStart, open);
        }
    }
    if (!name) {
        let operatorStart = cursor;
        while (operatorStart >= 0 && /[+\-*/%!~&|^<>=]/.test(text[operatorStart])) operatorStart--;
        let wordEnd = operatorStart;
        while (wordEnd >= 0 && /\s/.test(text[wordEnd])) wordEnd--;
        let wordStart = wordEnd;
        while (wordStart > 0 && isIdent(text[wordStart - 1])) wordStart--;
        if (text.slice(wordStart, wordEnd + 1) === 'operator') {
            name = 'operator' + text.slice(operatorStart + 1, cursor + 1).replace(/\s+/g, '');
            nameStart = wordStart;
        }
    }
    if (!name) return null;
    let before = nameStart - 1;
    while (before >= 0 && /\s/.test(text[before])) before--;
    if (before >= 0 && text[before] === '.') return null;
    if (keywords.has(name)) return null;
    let head = nameStart - 1;
    while (head >= 0 && text[head] !== ';' && text[head] !== '{' && text[head] !== '}') head--;
    const prefix = text.slice(head + 1, nameStart);
    if (/\b(?:class|struct|interface|record|enum|namespace|delegate|new)\b/.test(prefix)) return null;
    return { name, nameStart, start: skipSpace(text, head + 1) };
}

export function scan(source, file = '') {
    const text = maskSource(source);
    const lineAt = lineIndex(text);
    const methods = [];
    const bodies = [];
    for (let i = 0; i < text.length; i++) {
        if (text[i] !== '(') continue;
        if (bodies.some(body => i > body.start && i < body.end)) continue;
        const head = memberHead(text, i);
        if (!head) continue;
        const close = matchPair(text, i, '(', ')');
        if (close < 0) continue;
        let cursor = skipSpace(text, close + 1);
        let open = -1;
        let end = -1;
        for (let guard = 0; guard < 4 && open < 0; guard++) {
            if (text.startsWith('=>', cursor)) {
                open = cursor;
                end = text.indexOf(';', cursor + 2);
                if (end < 0) end = text.length - 1;
                break;
            }
            if (text[cursor] === '{') {
                open = cursor;
                end = matchPair(text, cursor, '{', '}');
                if (end < 0) end = text.length - 1;
                break;
            }
            const after = skipSpace(text, cursor + 1);
            if (text[cursor] === ':' && (wordIs(text, after, 'this') || wordIs(text, after, 'base'))) {
                const brace = text.indexOf('{', cursor + 1);
                if (brace < 0) break;
                cursor = brace;
                continue;
            }
            if (wordIs(text, cursor, 'where')) {
                const brace = text.indexOf('{', cursor);
                const arrow = text.indexOf('=>', cursor);
                const next = [brace, arrow].filter(value => value >= 0).sort((left, right) => left - right)[0];
                if (next === undefined) break;
                cursor = skipSpace(text, next);
                continue;
            }
            break;
        }
        if (open < 0) continue;
        methods.push({
            file,
            name: head.name,
            line: lineAt(head.nameStart),
            endLine: lineAt(end),
            start: head.start,
            end,
            cc: complexity(text.slice(head.start, end + 1)),
        });
        bodies.push({ start: open, end });
    }
    return methods;
}

export function changedLines(diff) {
    const files = new Map();
    let current = null;
    let inHunk = false;
    let lineCount = 0;
    for (const line of diff.split('\n')) {
        if (line.startsWith('diff --git ')) {
            current = null;
            inHunk = false;
            continue;
        }
        if (line.startsWith('+++ ')) {
            const target = line.slice(4).trim().replace(/^"|"$/g, '');
            current = target === '/dev/null' ? null : target.replace(/^b\//, '');
            if (current) files.set(current, []);
            inHunk = false;
            continue;
        }
        const hunk = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/);
        if (hunk) {
            inHunk = true;
            if (!current) continue;
            const start = Number(hunk[1]);
            const count = hunk[2] === undefined ? 1 : Number(hunk[2]);
            files.get(current).push(count > 0 ? [start, start + count - 1] : [start, start]);
            continue;
        }
        if (inHunk && (line.startsWith('+') || line.startsWith('-'))) lineCount++;
    }
    return { files, fileCount: files.size, lineCount };
}

export function violations(methods, ranges, limit, baseline) {
    const found = [];
    for (const method of methods) {
        const spans = ranges.get(method.file) ?? [];
        if (!spans.some(([from, to]) => method.line <= to && method.endLine >= from)) continue;
        const tolerated = Math.max(limit, baseline[method.file + '::' + method.name] ?? 0);
        if (method.cc > tolerated) found.push({ ...method, limit: tolerated });
    }
    return found.sort((left, right) => left.file.localeCompare(right.file) || left.line - right.line);
}

function git(args) {
    return execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function readBaseline() {
    try {
        return JSON.parse(fs.readFileSync(baselinePath, 'utf8'));
    } catch {
        return {};
    }
}

function writeBaseline() {
    const entries = new Map();
    for (const file of git(['ls-files', '*.cs']).split('\n').filter(Boolean)) {
        for (const method of scan(fs.readFileSync(path.join(root, file), 'utf8'), file)) {
            if (method.cc <= baselineLimit) continue;
            const key = file + '::' + method.name;
            entries.set(key, Math.max(entries.get(key) ?? 0, method.cc));
        }
    }
    const snapshot = Object.fromEntries([...entries.keys()].sort().map(key => [key, entries.get(key)]));
    fs.writeFileSync(baselinePath, JSON.stringify(snapshot, null, 2) + '\n');
    console.log('COMPLEXITY BASELINE methods=' + Object.keys(snapshot).length + ' file=Tools/complexity-baseline.json');
}

function resolveBase(requested) {
    const candidates = [];
    const add = ref => { if (ref && !candidates.includes(ref)) candidates.push(ref); };
    add(requested);
    add('origin/ai');
    if (process.env.GITHUB_BASE_REF) {
        add('origin/' + process.env.GITHUB_BASE_REF);
        add(process.env.GITHUB_BASE_REF);
    }
    add('origin/main');
    add('main');
    for (const ref of candidates) {
        try {
            const sha = git(['rev-parse', '--verify', '--quiet', ref + '^{commit}']).trim();
            if (sha) return { ref, sha, degraded: ref !== requested };
        } catch {
        }
    }
    return null;
}

function argument(name) {
    const inline = process.argv.find(arg => arg.startsWith('--' + name + '='));
    if (inline) return inline.slice(name.length + 3);
    const index = process.argv.indexOf('--' + name);
    return index >= 0 && process.argv[index + 1] && !process.argv[index + 1].startsWith('--') ? process.argv[index + 1] : null;
}

function check(since) {
    const requested = since ?? 'origin/ai';
    const base = resolveBase(requested);
    if (!base) {
        console.error('COMPLEXITY FAIL 无法解析基线引用 ' + requested + '，请先执行 git fetch origin ai');
        process.exitCode = 2;
        return;
    }
    if (base.degraded) console.error('COMPLEXITY NOTE 基线引用 ' + requested + ' 不可用，已降级到 ' + base.ref + ' ' + base.sha.slice(0, 12));
    const changed = changedLines(git(['diff', '--unified=0', '--no-color', '--no-ext-diff', base.sha + '...HEAD', '--', '*.cs']));
    const limit = threshold(changed.fileCount, changed.lineCount);
    const baseline = readBaseline();
    const methods = [];
    for (const file of changed.files.keys()) {
        const absolute = path.join(root, file);
        if (!fs.existsSync(absolute)) continue;
        methods.push(...scan(fs.readFileSync(absolute, 'utf8'), file));
    }
    const found = violations(methods, changed.files, limit, baseline);
    for (const method of found) console.log('COMPLEXITY FAIL ' + method.file + ':' + method.line + ' ' + method.name + ' ' + method.cc + ' > ' + method.limit);
    if (found.length) {
        process.exitCode = 1;
        return;
    }
    console.log('COMPLEXITY PASS files=' + changed.fileCount + ' lines=' + changed.lineCount + ' limit=' + limit + ' methods=' + methods.length + ' baseline=' + Object.keys(baseline).length + ' since=' + base.ref);
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    try {
        if (process.argv.includes('--baseline')) writeBaseline();
        else check(argument('since'));
    } catch (error) {
        console.error('COMPLEXITY FAIL ' + (error instanceof Error ? error.message : String(error)));
        process.exitCode = 2;
    }
}
