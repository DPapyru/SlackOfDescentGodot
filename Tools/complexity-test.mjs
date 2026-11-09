import assert from 'node:assert/strict';
import { threshold, scan, changedLines, violations } from './complexity.mjs';

const fixture = [
    'namespace Demo',
    '{',
    '    public class Sample',
    '    {',
    '        private string text = "if (a) { } // not code";',
    '        private string verbatim = @"class Fake { public int Fake(int x) { if (a) return 1; } }";',
    '        private string interpolated = $@"class Other { public int Other(int x) { return 1; } }";',
    '        public int Simple(int a)',
    '        {',
    '            if (a > 0) { return 1; }',
    '            return 2;',
    '        }',
    '        public int Branches(int a, bool b)',
    '        {',
    '            if (a > 0 && b || a < -1) { return a; }',
    '            for (int i = 0; i < a; i++) { a += i; }',
    '            foreach (var item in new int[] { 1, 2 }) { a += item; }',
    '            while (a > 10) { a--; }',
    '            switch (a) { case 1: break; case 2: break; default: break; }',
    '            try { } catch { }',
    '            int? value = null;',
    '            return (a > 0 ? 1 : 0) + (value ?? 0);',
    '        }',
    '        public int Expression(int a) => a > 0 ? a : -a;',
    '        public int Outer(int a)',
    '        {',
    '            int Inner(int b) { return b > 0 ? 1 : 0; }',
    '            return Inner(a);',
    '        }',
    '        public record Point(int X, int Y);',
    '    }',
    '}',
].join('\n');

const diff = [
    'diff --git a/Demo.cs b/Demo.cs',
    '--- a/Demo.cs',
    '+++ b/Demo.cs',
    '@@ -10,0 +11,5 @@',
    '+    int A()',
    '+    {',
    '+        if (x) { }',
    '+    }',
    '+',
    '@@ -30,2 +40,0 @@',
    '-    int B()',
    '-    {',
    'diff --git a/Gone.cs b/Gone.cs',
    '--- a/Gone.cs',
    '+++ /dev/null',
    '@@ -1,3 +0,0 @@',
    '-class Gone',
    '-{',
    '-}',
].join('\n');

const lineOf = needle => fixture.split('\n').findIndex(line => line.includes(needle)) + 1;
let count = 0;
const check = (label, run) => {
    try {
        run();
        count++;
    } catch (error) {
        throw new Error(label + ': ' + error.message);
    }
};

try {
    check('threshold', () => {
        assert.equal(threshold(0, 0), 8);
        assert.equal(threshold(2, 60), 8);
        assert.equal(threshold(2, 61), 20);
        assert.equal(threshold(6, 400), 20);
        assert.equal(threshold(6, 401), 25);
        assert.equal(threshold(7, 10), 25);
        assert.equal(threshold(20, 900), 25);
    });
    check('scan', () => {
        const methods = scan(fixture, 'Demo.cs');
        const byName = new Map(methods.map(method => [method.name, method]));
        assert.deepEqual([...byName.keys()], ['Simple', 'Branches', 'Expression', 'Outer']);
        assert.equal(byName.get('Simple').cc, 1);
        assert.equal(byName.get('Branches').cc, 11);
        assert.equal(byName.get('Expression').cc, 1);
        assert.equal(byName.get('Outer').cc, 1);
        assert.equal(byName.get('Simple').line, lineOf('public int Simple'));
        assert.equal(byName.get('Branches').line, lineOf('public int Branches'));
        assert.equal(byName.get('Expression').endLine, lineOf('public int Expression'));
    });
    check('changedLines', () => {
        const changed = changedLines(diff);
        assert.equal(changed.fileCount, 1);
        assert.equal(changed.lineCount, 10);
        assert.deepEqual(changed.files.get('Demo.cs'), [[11, 15], [40, 40]]);
        assert.equal(changed.files.has('Gone.cs'), false);
    });
    check('baseline', () => {
        const methods = scan(fixture, 'Demo.cs');
        const all = new Map([['Demo.cs', [[1, fixture.split('\n').length]]]]);
        const found = violations(methods, all, 8, {});
        assert.deepEqual(found.map(method => method.name), ['Branches']);
        assert.equal(found[0].cc, 11);
        assert.equal(violations(methods, all, 8, { 'Demo.cs::Branches': 11 }).length, 0);
        assert.equal(violations(methods, all, 8, { 'Demo.cs::Branches': 9 }).length, 1);
        const simple = new Map([['Demo.cs', [[lineOf('public int Simple'), lineOf('public int Simple')]]]]);
        assert.equal(violations(methods, simple, 8, {}).length, 0);
    });
    console.log('COMPLEXITY TEST PASS assertions=' + count);
} catch (error) {
    console.error('COMPLEXITY TEST FAIL ' + error.message);
    process.exitCode = 1;
}
