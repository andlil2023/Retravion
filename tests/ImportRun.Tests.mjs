import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { plan, approve, digest, canonical } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-plan.mjs';
import { ImportRunner, readJournal, report } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-run.mjs';
import { RetravionTerminal } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/server.mjs';

const result = value => ({ content: [{ type: 'text', text: JSON.stringify(value) }] });
const error = message => ({ isError: true, content: [{ type: 'text', text: message }] });
const hash = v => createHash('sha256').update(canonical(v)).digest('hex');
const id = n => `10000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const section = (sourceKey, kind, fields, relationships = []) => `## ${sourceKey}\n\`\`\`retravion\n${JSON.stringify({ kind, fields, relationships })}\n\`\`\`\n`;
async function fixture(t, options = {}) {
    const directory = await mkdtemp(join(tmpdir(), 'rtv-import-'));
    t.after(() => rm(directory, { recursive: true, force: true }));
    const manifest = plan('P', [
        { file: 'needs.md', content: section('n', 'user-need', { id: 'UN-1', title: 'Need', statement: '**Need**\n\ntext' }) },
        { file: 'design.md', content: section('r', 'requirement', { id: 'REQ-1', title: 'Req', statement: 'Shall', type: 'Software' }, [{ relationship: 'user-need', target: { sourceKey: 'n' } }]) + section('t', 'test-case', { steps: [{ action: 'First', expectedResult: 'One' }, { action: 'Second', expectedResult: 'Two' }] }, [{ relationship: 'verifies', target: { sourceKey: 'r' } }]) + section('f', 'feature', {}, [{ relationship: 'addresses', target: { sourceKey: 'r' } }]) }
    ]);
    const manifestPath = join(directory, 'manifest.json'), approvalPath = join(directory, 'approval.json');
    await writeFile(manifestPath, JSON.stringify(manifest));
    await writeFile(approvalPath, JSON.stringify(approve(manifest, digest(manifest), 'P')));
    const calls = [], rows = new Map(); let number = 0;
    const server = async (name, args, signal) => {
        calls.push({ name, args: structuredClone(args) });
        const special = await options.intercept?.(name, args, rows, calls, signal);
        if (special) return special;
        if (name === 'read_product') return result({ id: 'P', status: 'Active' });
        if (name.startsWith('create_draft_')) {
            const kind = name.slice(13).replaceAll('_', '-');
            const fields = { ...args }; delete fields.productId;
            const row = { ...fields, id: fields.id ?? (kind === 'feature' ? 'F-' : 'TC-') + ++number,
                internalId: id(++number), versionId: id(++number), versionNumber: 1, revision: 1, productInternalId: id(999), status: 'Draft', links: [], kind };
            rows.set(row.internalId, row); return result(row);
        }
        if (name.startsWith('read_')) {
            const row = rows.get(args.internalId);
            if (!row || row.versionId !== args.versionId) return error('Not found.');
            const wrapper = row.kind === 'user-need' ? 'need' : row.kind === 'test-case' ? 'testCase' : row.kind;
            return result({ [wrapper]: row });
        }
        if (name === 'change_draft_link') {
            const row = rows.get(args.ownerId);
            if (row.revision !== args.expectedRevision) return error('Stale revision.');
            row.links.push({ internalId: args.targetId, versionId: args.targetVersionId }); row.revision++;
            return result(args);
        }
        if (name === 'explore_relationships') {
            const row = rows.get(args.internalId);
            return result({ truncated: false, edges: row.links.map(link => ({ owner: row, target: rows.get(link.internalId), relationship: 'observed', isProvenance: false })) });
        }
        if (name.startsWith('list_')) return result({ items: [...rows.values()].filter(r => r.kind === name.slice(5).replace(/s$/, '').replaceAll('_', '-')), nextOffset: null });
        throw new Error('Unexpected test tool ' + name);
    };
    const runner = new ImportRunner(server, options);
    const createArgs = { action: 'create', manifestPath, approvalPath, runDirectory: join(directory, 'create'), confirmedProduct: 'P', confirmed: true };
    const previewArgs = { action: 'preview-links', manifestPath, creationRunDirectory: createArgs.runDirectory, proposalPath: join(directory, 'links.json') };
    return { directory, manifest, manifestPath, runner, calls, rows, createArgs, previewArgs,
        async links() { return runner.run({ action: 'links', manifestPath, proposalPath: previewArgs.proposalPath, runDirectory: join(directory, 'links-run'), approvedSha256: hash(await JSON.parse(await readFile(previewArgs.proposalPath, 'utf8'))), confirmedProduct: 'P', confirmed: true }); } };
}
const writes = calls => calls.filter(c => c.name.startsWith('create_draft_') || c.name === 'change_draft_link');

test('approved multi-file fields, dependency mapping, compact results and separately approved exact links', async t => {
    const f = await fixture(t);
    const created = await f.runner.run(f.createArgs);
    assert.equal(created.counts['confirmed-success'], 4);
    assert.equal(writes(f.calls).length, 4);
    assert.ok(created.operations.every(o => o.identity && !o.fields));
    const durable = JSON.parse(await readFile(join(f.directory, 'create/report.json'), 'utf8'));
    assert.deepEqual(durable.operations.find(o => o.kind === 'test-case').fields.steps, f.manifest.records.find(o => o.kind === 'test-case').fields.steps);
    for (const c of writes(f.calls)) {
        const row = f.manifest.records.find(r => `create_draft_${r.kind.replaceAll('-', '_')}` === c.name);
        assert.deepEqual(c.args, { productId: 'P', ...row.fields });
        assert.equal(Object.hasOwn(c.args, 'actor'), false); assert.equal(Object.hasOwn(c.args, 'status'), false);
    }
    const preview = await f.runner.run(f.previewArgs);
    assert.equal(preview.proposal.operations.length, 3);
    assert.equal(writes(f.calls).length, 4, 'preview is read-only');
    assert.ok(preview.proposal.operations.every(o => o.owner.versionId && o.target.versionId && o.owner.revision === 1));
    const linked = await f.links();
    assert.equal(linked.counts['confirmed-success'], 3);
    assert.equal(writes(f.calls).length, 7);
    const reconciled = await f.runner.run({ action: 'reconcile', runDirectory: join(f.directory, 'links-run'), reportPath: join(f.directory, 'link-observations.json') });
    assert.ok(reconciled.observations.every(o => !o.incomplete && o.observedEdges.length === 1));
    assert.equal(writes(f.calls).length, 7);
    assert.equal((await f.links()).error.includes('Local'), true, 'same proposal cannot be replayed');
    const replay = await f.runner.run({ ...f.createArgs, runDirectory: join(f.directory, 'another') });
    assert.ok(replay.error); assert.equal(writes(f.calls).length, 7);
});

test('changed plans, denied consent, wrong Product and unsupported action dispatch zero writes', async t => {
    const f = await fixture(t);
    for (const args of [{ ...f.createArgs, confirmed: false }, { ...f.createArgs, confirmedProduct: 'Q' }, { ...f.createArgs, actor: 'injected' }, { ...f.createArgs, action: 'edit' }]) assert.ok((await f.runner.run(args)).error);
    f.manifest.records[0].fields.title = 'changed'; await writeFile(f.manifestPath, JSON.stringify(f.manifest));
    assert.ok((await f.runner.run(f.createArgs)).error);
    assert.equal(writes(f.calls).length, 0);
});

for (const message of ['Access denied.', 'Stale revision.', 'Audit submission failed.', 'The write outcome could not be confirmed.', 'Retravion did not answer. Its outcome is unknown.']) {
    test('stop on server failure without replay: ' + message, async t => {
        let count = 0;
        const f = await fixture(t, { intercept: name => name.startsWith('create_draft_') && ++count === 2 ? error(message) : null });
        const summary = await f.runner.run(f.createArgs);
        assert.equal(summary.counts['confirmed-success'], 1);
        assert.equal(summary.counts['not-attempted'], 2);
        assert.equal(summary.counts[/confirmed|unknown/.test(message) ? 'uncertain' : 'rejected'], 1);
        assert.equal(writes(f.calls).length, 2);
        assert.ok((await f.runner.run(f.createArgs)).error);
        assert.equal(writes(f.calls).length, 2);
        assert.doesNotMatch(JSON.stringify(summary), /Audit submission failed/);
    });
}

test('lost responses, generated-ID candidates and partial journals require reconciliation, never recreation', async t => {
    const f = await fixture(t, { intercept: (name, args, rows) => {
        if (name === 'create_draft_feature') {
            for (const n of [70, 80]) rows.set(id(n), { kind: 'feature', id: 'F-' + n, title: '', description: '', internalId: id(n), versionId: id(n + 1), versionNumber: 1, revision: 1, productInternalId: id(999), status: 'Draft' });
            throw new Error('private token=DO_NOT_LEAK');
        }
    } });
    const summary = await f.runner.run(f.createArgs);
    assert.equal(summary.counts.uncertain, 1);
    const before = writes(f.calls).length;
    const reconciled = await f.runner.run({ action: 'reconcile', runDirectory: f.createArgs.runDirectory, reportPath: join(f.directory, 'reconciled.json') });
    const candidate = reconciled.observations.find(o => o.observation === 'candidates-only-not-proof');
    assert.equal(candidate.ambiguous, true); assert.equal(candidate.candidates.length, 2);
    assert.equal(writes(f.calls).length, before);
    assert.doesNotMatch(JSON.stringify(reconciled), /DO_NOT_LEAK|token=/);
    const journal = join(f.createArgs.runDirectory, 'journal.jsonl');
    await writeFile(journal, (await readFile(journal, 'utf8')) + '{"type":');
    assert.equal(report(await readJournal(f.createArgs.runDirectory)).counts.uncertain, 1);
});

test('checkpoint failure before dispatch is no write; failure after confirmed success never replays', async t => {
    for (const type of ['dispatch', 'outcome']) {
        let failed = false;
        const f = await fixture(t, { checkpoint: event => { if (!failed && event.type === type) { failed = true; throw new Error('private disk provider'); } } });
        const summary = await f.runner.run(f.createArgs);
        assert.equal(summary.checkpointFailed, true);
        assert.equal(writes(f.calls).length, type === 'dispatch' ? 0 : 1);
        if (type === 'outcome') {
            assert.equal(summary.counts['confirmed-success'], 1);
            const durable = report(await readJournal(f.createArgs.runDirectory));
            assert.equal(durable.counts.uncertain, 1, 'dispatch without outcome is uncertain');
        }
        assert.ok((await f.runner.run(f.createArgs)).error);
        assert.equal(writes(f.calls).length, type === 'dispatch' ? 0 : 1);
    }
});

test('interruption stops after current operation and never resumes later operations', async t => {
    const controller = new AbortController();
    const f = await fixture(t, { checkpoint: event => { if (event.type === 'outcome') controller.abort(); } });
    const summary = await f.runner.run(f.createArgs, controller.signal);
    assert.equal(summary.counts['confirmed-success'], 1); assert.equal(summary.counts['not-attempted'], 3);
    assert.equal(writes(f.calls).length, 1);
});

test('stale exact endpoints and changed link proposal stop before mutation', async t => {
    const f = await fixture(t); await f.runner.run(f.createArgs); const p = await f.runner.run(f.previewArgs);
    const owner = f.rows.get(p.proposal.operations[0].owner.internalId); owner.revision++;
    const linked = await f.links();
    assert.equal(linked.counts.rejected, 1); assert.equal(writes(f.calls).length, 4);
    const g = await fixture(t); await g.runner.run(g.createArgs); const preview = await g.runner.run(g.previewArgs);
    preview.proposal.operations[0].owner.internalId = id(888);
    await writeFile(g.previewArgs.proposalPath, JSON.stringify(preview.proposal));
    assert.ok((await g.links()).error); assert.equal(writes(g.calls).length, 4);
});

test('multiple links refresh owner revisions after own changes', async t => {
    const f = await fixture(t);
    const req = f.manifest.records.find(r => r.kind === 'requirement');
    const other = structuredClone(req); other.sourceKey = 'r2'; other.source.section = 'r2'; other.fields.id = 'REQ-2'; other.relationships = [];
    // Replan a valid additional requirement and second Feature link.
    const records = [...f.manifest.records, other].map(r => {
        const relationships = r.kind === 'feature' ? [...r.relationships, { relationship: 'addresses', target: { sourceKey: 'r2' } }] : r.relationships;
        return section(r.sourceKey, r.kind, r.fields, relationships);
    }).join('');
    const manifest = plan('P', [{ file: 'all.md', content: records }]);
    await writeFile(f.manifestPath, JSON.stringify(manifest)); await writeFile(f.createArgs.approvalPath, JSON.stringify(approve(manifest, digest(manifest), 'P')));
    await f.runner.run(f.createArgs); await f.runner.run(f.previewArgs); const summary = await f.links();
    assert.equal(summary.counts['confirmed-success'], 4);
    const featureLinks = f.calls.filter(c => c.name === 'change_draft_link' && c.args.ownerKind === 'feature');
    assert.deepEqual(featureLinks.map(c => c.args.expectedRevision), [1, 2]);
});

test('bridge import tool requires live connection and forwards whole import without model turns', async t => {
    const f = await fixture(t);
    const requests = [];
    const terminal = new RetravionTerminal({ endpoint: 'https://localhost:8443/mcp', tools: [{ name: 'read_product' }, ...Object.values({ n: 'user_need', r: 'requirement', t: 'test_case', f: 'feature' }).map(k => ({ name: 'create_draft_' + k }))], open: async () => true,
        request: async (url, options) => {
            if (url.pathname.endsWith('/start')) return { status: 200, headers: {}, text: JSON.stringify({ deviceCode: 'A'.repeat(64), userCode: 'ABCD-EFGH', verificationUri: 'https://localhost:8443/settings/ai-workflow', expiresIn: 600 }) };
            if (url.pathname.endsWith('/status')) return { status: 200, headers: {}, text: '{"status":"approved"}' };
            const body = JSON.parse(options.body); requests.push(body);
            if (body.method === 'notifications/initialized') return { status: 202, headers: {}, text: '' };
            const response = body.method === 'tools/call' ? await f.runner.call(body.params.name, body.params.arguments) : {};
            return { status: 200, headers: {}, text: JSON.stringify({ jsonrpc: '2.0', id: body.id, result: response }) };
        } });
    assert.equal((await terminal.call('rtv_import', f.createArgs)).isError, true);
    assert.equal(requests.length, 0);
    await terminal.call('rtv_connect'); await terminal.call('rtv_complete_connection');
    const summary = JSON.parse((await terminal.call('rtv_import', f.createArgs)).content[0].text);
    assert.equal(summary.counts['confirmed-success'], 4);
    assert.equal(requests.filter(r => r.method === 'tools/call').length, 5);
});
