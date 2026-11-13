import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { plan, preview, digest, approve, verifyApproval, validateManifest, main } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-plan.mjs';

const script = fileURLToPath(new URL('../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-plan.mjs', import.meta.url));
const record = (key, kind, fields, relationships) => `## ${key}\n\n\`\`\`retravion\n${JSON.stringify({ kind, fields, ...(relationships ? { relationships } : {}) }, null, 2)}\n\`\`\`\n`;
const link = (relationship, sourceKey) => ({ relationship, target: { sourceKey } });
const need = { id: ' UN-1 ', title: 'Need', statement: '# Markdown\n\n**Keep** trailing space  \n\n```js\nalert(1);\n```\n' };
const requirement = { id: 'REQ-1', title: 'Requirement', statement: 'Shall preserve\norder.', type: 'Software' };
const sources = () => [
    { file: 'needs.md', content: record('need', 'user-need', need) },
    { file: 'design.md', content: record('test', 'test-case', { steps: [{ action: 'second named first', expectedResult: '' }, { action: '**Next**', expectedResult: 'done' }] }, [link('verifies', 'req')]) + record('req', 'requirement', requirement, [link('user-need', 'need')]) + record('feature', 'feature', {}, [link('addresses', 'req')]) }
];
const single = (kind, fields, relationships) => plan('P', [{ file: 'source.md', content: record('key', kind, fields, relationships) }]);

test('all four kinds, multiple files, Markdown and step order, defaults and source locations', () => {
    const m = plan('P', sources());
    assert.equal(m.records.length, 4);
    assert.deepEqual(m.dependencyOrder, ['need', 'req', 'feature', 'test']);
    const get = kind => m.records.find(r => r.kind === kind);
    assert.equal(get('user-need').fields.id, 'UN-1');
    assert.equal(get('user-need').fields.statement, need.statement);
    assert.deepEqual(get('feature').fields, { title: '', description: '' });
    assert.equal(get('requirement').fields.rationale, null);
    assert.deepEqual(get('test-case').fields, { title: '', description: '', preconditions: '', steps: [{ action: 'second named first', expectedResult: '' }, { action: '**Next**', expectedResult: 'done' }] });
    assert.deepEqual(get('user-need').source, { file: 'needs.md', section: 'need', line: 1 });
    assert.equal(Object.hasOwn(get('feature').fields, 'id'), false);
    const p = preview(m);
    assert.ok(p.includes(JSON.stringify(m, null, 2)));
    for (const value of ['areaId=null', 'Draft v1', 'no links', 'server-generated', 'NOT approved', 'rationale', 'preconditions']) assert.ok(p.includes(value));
});

test('deterministic source argument order, JSON whitespace/property order and CRLF', () => {
    const a = plan('P', sources());
    const b = plan('P', sources().reverse().map(s => ({ ...s, content: s.content.replaceAll('\n', '\r\n') })));
    assert.deepEqual(a, b);
    assert.equal(digest(a), digest(b));
    const x = single('feature', { title: 'T', description: 'D' });
    const y = single('feature', { description: 'D', title: 'T' });
    assert.deepEqual(x, y);
    assert.deepEqual(single('test-case', {}), single('test-case', { title: null, description: null, preconditions: null, steps: null }));
});

test('actionable malformed/unsupported/ambiguous structures and required values', () => {
    for (const [content, message] of [
        ['# Loose prose', /source.md:1.*unsupported structure/],
        ['## key\n```json\n{}\n```', /expected a retravion JSON fence/],
        ['## key\n```retravion\n{}', /unclosed/],
        ['## key\n```retravion\n{bad}\n```', /source.md:1.*Invalid JSON/],
        ['## key\n```retravion\n{"kind":"feature","fields":{"title":"one","title":"two"}}\n```', /Duplicate JSON member/],
        ['## key\n```retravion\n{"kind":"feature","fields":{"title":"one","t\\u0069tle":"two"}}\n```', /Duplicate JSON member/],
    ]) assert.throws(() => plan('P', [{ file: 'source.md', content }]), message);
    assert.throws(() => single('requirement', { ...requirement, type: undefined }), /explicitly select/);
    assert.throws(() => single('requirement', { ...requirement, type: 'inferred' }), /explicitly select/);
    assert.throws(() => single('user-need', { ...need, statement: '' }), /statement.*nonempty/);
    assert.throws(() => single('feature', { id: 'NOT-SERVER-ID' }), /unsupported field/);
    assert.throws(() => single('feature', { token: 'never persist this' }), /unsupported field/);
    assert.throws(() => single('test-case', { steps: [{ action: 'x' }] }), /expectedResult/);
    assert.throws(() => single('test-case', { steps: [null] }), /expected an object/);
    assert.throws(() => single('user-need', { ...need, id: '😀'.repeat(101) }), /maximum 100/);
    assert.doesNotThrow(() => single('user-need', { ...need, id: '😀'.repeat(100) }));
    assert.throws(() => plan('', sources()), /Product explicitly selected/);
    assert.throws(() => plan('P', []), /No records/);
});

test('duplicate keys/IDs, unresolved references, wrong relationships and cycles', () => {
    assert.throws(() => plan('P', [...sources(), { file: 'other.md', content: record('need', 'feature', {}) }]), /duplicate source key/);
    assert.throws(() => plan('P', [...sources(), { file: 'other.md', content: record('other', 'user-need', need) }]), /duplicate manual ID/);
    assert.throws(() => plan('P', [...sources(), sources()[0]]), /duplicate input file/);
    assert.throws(() => single('test-case', {}, [link('verifies', 'missing')]), /source.md:1.*unresolved/);
    assert.throws(() => plan('P', [{ file: 'a.md', content: record('a', 'requirement', requirement, [link('parent', 'b')]) + record('b', 'requirement', { ...requirement, id: 'REQ-2' }, [link('parent', 'a')]) }]), /dependency cycle/);
    assert.throws(() => single('requirement', requirement, [link('parent', 'key')]), /dependency cycle/);
    assert.throws(() => single('requirement', requirement, [link('parent', 'a'), link('parent', 'b')]), /only one parent/);
    assert.throws(() => plan('P', [{ file: 'a.md', content: record('n', 'user-need', need) + record('t', 'test-case', {}, [link('verifies', 'n')]) }]), /wrong target kind/);
    const target = { kind: 'requirement', productId: 'P', internalId: '11111111-1111-1111-1111-111111111111', versionId: '22222222-2222-2222-2222-222222222222', versionNumber: 2 };
    assert.doesNotThrow(() => single('feature', {}, [{ relationship: 'addresses', target }]));
    assert.throws(() => single('feature', {}, [{ relationship: 'addresses', target: { ...target, productId: 'OTHER' } }]), /Product/);
    assert.throws(() => single('feature', {}, [{ relationship: 'addresses', target: { ...target, versionId: undefined } }]), /exact versionId/);
    assert.throws(() => single('feature', {}, [{ relationship: 'addresses', target }, { relationship: 'addresses', target }]), /duplicate relationship/);
});

test('.NET whitespace parity preserves exact initial values and duplicate-ID detection', () => {
    const whitespace = '\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000';
    for (const char of whitespace) {
        for (const kind of ['user-need', 'requirement']) {
            const fields = kind === 'requirement' ? requirement : need;
            assert.equal(single(kind, { ...fields, id: char + 'ID' + char }).records[0].fields.id, 'ID');
            assert.throws(() => single(kind, { ...fields, title: char }), /nonempty/);
            assert.throws(() => single(kind, { ...fields, statement: char }), /nonempty/);
            assert.throws(() => plan('P', [{ file: 'a.md', content: record('a', kind, { ...fields, id: 'ID' }) + record('b', kind, { ...fields, id: char + 'ID' + char }) }]), /duplicate manual ID/);
        }
    }
    // JS removes FEFF; .NET preserves it, including for nonblank validation.
    assert.equal(single('user-need', { ...need, id: '\uFEFFID\uFEFF', title: '\uFEFF' }).records[0].fields.id, '\uFEFFID\uFEFF');
    assert.throws(() => plan('\u0085', sources()), /Product explicitly selected/);
});

test('approval binds every byte of canonical proposal, Product, defaults, steps and endpoints', () => {
    const m = plan('P', sources()), hash = digest(m), approval = approve(m, hash, 'P');
    assert.equal(verifyApproval(m, approval), true);
    assert.throws(() => approve(m, hash, 'other'), /Product differs/);
    assert.throws(() => approve(m, 'old hash', 'P'), /differs/);
    for (const mutate of [
        x => { x.productId = 'other'; x.records.forEach(r => r.productId = 'other'); },
        x => { x.records[0].fields.title = 'changed'; },
        x => { x.records.find(r => r.kind === 'test-case').fields.steps.reverse(); },
        x => { x.records.find(r => r.kind === 'requirement').fields.rationale = ''; },
        x => { x.records.find(r => r.kind === 'feature').relationships = []; },
        x => { x.records[0].source.file = 'changed.md'; },
        x => { x.records[0].fields.actor = 'forbidden'; },
        x => { x.records[0].fields.title = undefined; },
    ]) {
        const changed = structuredClone(m); mutate(changed);
        assert.throws(() => verifyApproval(changed, approval));
    }
    assert.throws(() => verifyApproval(m, { ...approval, scope: 'links-too' }), /Invalid creation approval/);
    assert.throws(() => validateManifest({ ...m, dependencyOrder: [] }), /not canonical/);
});

test('CLI artifacts, approval, tamper rejection and zero mutation/network capabilities', async () => {
    const temp = await mkdtemp(join(tmpdir(), 'retravion-plan-'));
    try {
        const files = [];
        for (const s of sources()) { const file = join(temp, s.file); await writeFile(file, s.content); files.push(file); }
        const output = join(temp, 'artifacts');
        const run = (...args) => spawnSync(process.execPath, [script, ...args], { encoding: 'utf8' });
        const result = run('plan', 'P', output, ...files);
        assert.equal(result.status, 0, result.stderr);
        const path = join(output, 'manifest.json'), approvalPath = join(temp, 'approval.json');
        const m = JSON.parse(await readFile(path, 'utf8'));
        assert.equal(await readFile(join(output, 'preview.txt'), 'utf8'), preview(m));
        assert.equal(run('approve', path, digest(m), 'P', approvalPath).status, 0);
        assert.equal(run('check', path, approvalPath).status, 0);
        assert.equal(run('approve', path, digest(m), 'P', approvalPath).status, 1, 'never overwrite approval');
        m.records[0].fields.title = 'changed'; await writeFile(path, JSON.stringify(m));
        assert.equal(run('check', path, approvalPath).status, 1);
        assert.equal(run('execute', path, approvalPath).status, 1, 'no execution command');
        await writeFile(files[0], '# Unsupported');
        const invalid = run('plan', 'P', join(temp, 'invalid'), ...files);
        assert.equal(invalid.status, 1);
        assert.match(invalid.stderr, /needs.md:1.*unsupported structure/);
        await assert.rejects(readFile(join(temp, 'invalid', 'manifest.json')), /ENOENT/);
        // All capabilities are audited statically: the planner cannot reach MCP, network or child processes.
        const code = await readFile(script, 'utf8');
        const imports = [...code.matchAll(/^import .* from '([^']+)';/gm)].map(m => m[1]);
        assert.deepEqual(imports, ['node:crypto', 'node:fs/promises', 'node:path', 'node:url']);
        assert.doesNotMatch(code, /\b(fetch|https|http|RetravionTerminal|execFile|spawn|process\.env|tools\/call)\b/);
        assert.match(await main(['preview', path]), /changed/);
    } finally { await rm(temp, { recursive: true, force: true }); }
});
