// Test/benchmark adapter: real authenticated MCP CallToolResults arrive from the .NET test host.
// No credentials or production endpoints are read. Not part of the distributed integration.
import { createInterface } from 'node:readline';
import { mkdtemp, writeFile, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import assert from 'node:assert/strict';
import { plan, digest, approve } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-plan.mjs';
import { ImportRunner } from '../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/import-run.mjs';
const lines = createInterface({ input: process.stdin })[Symbol.asyncIterator]();
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
let requests = 0;
const call = async (name, args) => { requests++; send({ type: 'call', name, args }); const response = await lines.next(); if (response.done) throw new Error('Test transport ended.'); return JSON.parse(response.value); };
const [mode] = process.argv.slice(2);
const directory = await mkdtemp(join(tmpdir(), 'rtv-real-import-'));
const section = (key, kind, fields, relationships = []) => `## ${key}\n\`\`\`retravion\n${JSON.stringify({ kind, fields, relationships })}\n\`\`\`\n`;
function dataset(productId, groups) {
    let needs = '', design = '';
    for (let i = 1; i <= groups; i++) {
        needs += section('n' + i, 'user-need', { id: 'UN-' + i, title: 'Need ' + i, statement: '**Markdown**\n\nNeed ' + i });
        design += section('r' + i, 'requirement', { id: 'REQ-' + i, title: 'Requirement ' + i, statement: 'Shall ' + i, type: 'Software', rationale: '' }, [{ relationship: 'user-need', target: { sourceKey: 'n' + i } }]);
        design += section('t' + i, 'test-case', { title: 'Case ' + i, description: '**Procedure**', preconditions: '', steps: [{ action: 'First', expectedResult: '' }, { action: 'Second', expectedResult: 'Done' }] }, [{ relationship: 'verifies', target: { sourceKey: 'r' + i } }]);
        design += section('f' + i, 'feature', { title: 'Feature ' + i, description: '**Description**' }, [{ relationship: 'addresses', target: { sourceKey: 'r' + i } }]);
    }
    return plan(productId, [{ file: 'needs.md', content: needs }, { file: 'design.md', content: design }]);
}
async function prepare(productId, groups) {
    const manifest = dataset(productId, groups), manifestPath = join(directory, productId + '.json'), approvalPath = manifestPath + '.approval';
    await writeFile(manifestPath, JSON.stringify(manifest));
    await writeFile(approvalPath, JSON.stringify(approve(manifest, digest(manifest), productId)));
    return { manifest, manifestPath, approvalPath };
}
try {
    const runner = new ImportRunner(call);
    const bulk = await prepare('P-import', mode === 'benchmark' ? 3 : 1);
    const before = requests, start = performance.now();
    const created = await runner.run({ action: 'create', ...Object.fromEntries(Object.entries(bulk).filter(([k]) => k !== 'manifest')), runDirectory: join(directory, 'create'), confirmedProduct: 'P-import', confirmed: true });
    if (mode !== 'benchmark') {
        assert.equal(created.counts['confirmed-success'], 2);
        assert.equal(created.counts.uncertain, 1); assert.equal(created.counts['not-attempted'], 1);
        const observed = await runner.run({ action: 'reconcile', runDirectory: join(directory, 'create'), reportPath: join(directory, 'reconciliation.json') });
        assert.equal(observed.observations.find(o => o.sourceKey === 'f1').observation, 'candidates-only-not-proof');
        const count = requests;
        assert.ok((await runner.run({ action: 'create', manifestPath: bulk.manifestPath, approvalPath: bulk.approvalPath, runDirectory: join(directory, 'retry'), confirmedProduct: 'P-import', confirmed: true })).error);
        assert.equal(requests, count + 1, 'only Product read on forbidden replay');
        send({ type: 'result', created, observations: observed.observations });
    } else {
        assert.equal(created.counts['confirmed-success'], 12, JSON.stringify(created));
        const proposalPath = join(directory, 'links.json');
        const preview = await runner.run({ action: 'preview-links', manifestPath: bulk.manifestPath, creationRunDirectory: join(directory, 'create'), proposalPath });
        const linked = await runner.run({ action: 'links', manifestPath: bulk.manifestPath, proposalPath, approvedSha256: preview.sha256, runDirectory: join(directory, 'links'), confirmedProduct: 'P-import', confirmed: true });
        assert.equal(linked.counts['confirmed-success'], 9, JSON.stringify(linked));
        const bulkMetrics = { elapsedMs: performance.now() - start, mcpRequests: requests - before, modelTurns: null, tokenUsage: null, scriptedToolReturns: 3 };
        // Equivalent existing per-record control flow, without real model/human think time.
        const manual = dataset('P-baseline', 3), mappings = new Map();
        const baselineBefore = requests, baselineStart = performance.now(); let scriptedToolReturns = 0;
        const one = async (name, args) => { scriptedToolReturns++; const response = await call(name, args); assert.notEqual(response.isError, true, JSON.stringify(response)); return JSON.parse(response.content[0].text); };
        await one('read_product', { productId: manual.productId });
        for (const key of manual.dependencyOrder) {
            const r = manual.records.find(r => r.sourceKey === key);
            mappings.set(key, await one('create_draft_' + r.kind.replaceAll('-', '_'), { productId: manual.productId, ...r.fields }));
        }
        for (const r of manual.records) for (const link of r.relationships) {
            const endpoint = mappings.get(r.sourceKey), target = mappings.get(link.target.sourceKey);
            const read = async (kind, e) => one('read_' + kind.replaceAll('-', '_'), { internalId: e.internalId, versionId: e.versionId });
            const wrapper = r.kind === 'test-case' ? 'testCase' : r.kind;
            const owner = (await read(r.kind, endpoint))[wrapper];
            await read(manual.records.find(x => x.sourceKey === link.target.sourceKey).kind, target);
            await one('change_draft_link', { ownerKind: r.kind, ownerId: owner.internalId, ownerVersionId: owner.versionId, expectedRevision: owner.revision, relationship: link.relationship, operation: 'add', targetId: target.internalId, targetVersionId: target.versionId, confirmed: true });
            await read(r.kind, endpoint);
        }
        const baselineMetrics = { elapsedMs: performance.now() - baselineStart, mcpRequests: requests - baselineBefore, modelTurns: null, tokenUsage: null, scriptedToolReturns };
        const localReport = JSON.parse(await readFile(join(directory, 'create/report.json'), 'utf8'));
        assert.equal(localReport.operations.length, 12);
        send({ type: 'result', bulk: bulkMetrics, perRecordScriptedBaseline: baselineMetrics, dataset: '3 groups: 12 records across 2 Markdown files, 9 generated-endpoint links; separate Products with equivalent values', limitations: 'In-process authenticated ASP.NET test host + disposable PostgreSQL; no live model turns, token telemetry, user approval latency, real TLS or network latency. Scripted tool returns are not model turns. Single sample; includes import journal fsync. No promised savings.' });
    }
} finally { await rm(directory, { recursive: true, force: true }); process.stdin.destroy(); }
