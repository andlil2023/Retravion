// Approved client-side orchestration only. All server access goes through the live bridge.
import { open, readFile, mkdir } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { canonical, digest, validateManifest, verifyApproval } from './import-plan.mjs';

const spec = {
    'user-need': ['user_need', 'user_needs', 'need'],
    requirement: ['requirement', 'requirements', 'requirement'],
    'test-case': ['test_case', 'test_cases', 'testCase'],
    feature: ['feature', 'features', 'feature'],
};
const stop = message => { throw new Error(message); };
const hash = value => createHash('sha256').update(canonical(value)).digest('hex');
const uuid = v => typeof v === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(v);
const json = async path => JSON.parse(await readFile(path, 'utf8'));
const positive = v => Number.isSafeInteger(v) && v > 0;
const exactKeys = (value, keys) => {
    if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).length !== keys.length || keys.some(k => !Object.hasOwn(value, k))) stop('Unsupported proposal fields.');
};
const safeError = 'Local validation, approval or file operation failed. Inspect the local artifacts; no automatic retry. Reconcile any previously dispatched work.';
const uncertain = /uncertain|unknown|could not be confirmed|did not answer|no response|do not retry|never retry/i;
export const importTools = [{ name: 'rtv_import', description: 'Approved local import workflow. Load the skill first. action=create uses manifestPath, approvalPath, runDirectory, confirmedProduct and confirmed=true after explicit creation approval. preview-links uses manifestPath, creationRunDirectory and proposalPath (new file), reads exact endpoints only. links uses manifestPath, proposalPath, approvedSha256 from exact endpoint preview, runDirectory, confirmedProduct and confirmed=true after separate explicit endpoint approval. reconcile uses runDirectory and reportPath (new file), reads only; never retries. New run directories and single-use approval claims prevent resume/replay. No lifecycle, content edits, classification or review saves.', inputSchema: {
    type: 'object', additionalProperties: false, properties: Object.fromEntries([
        ['action', { type: 'string', enum: ['create', 'preview-links', 'links', 'reconcile'] }],
        ...['manifestPath', 'approvalPath', 'runDirectory', 'creationRunDirectory', 'proposalPath', 'reportPath', 'approvedSha256', 'confirmedProduct'].map(k => [k, { type: 'string' }]), ['confirmed', { type: 'boolean' }]
    ]), required: ['action']
} }];

function payload(result) {
    if (result?.isError) {
        const message = result.content?.filter(c => c.type === 'text').map(c => c.text).join(' ') ?? '';
        return { outcome: result.importOutcome ?? (uncertain.test(message) ? 'uncertain' : 'rejected') };
    }
    try {
        const content = result.content.filter(c => c.type === 'text');
        if (content.length !== 1) return { outcome: 'uncertain' };
        const value = JSON.parse(content[0].text);
        if (value?.isSuccess === false) return { outcome: 'uncertain' };
        return { outcome: 'confirmed-success', value };
    } catch { return { outcome: 'uncertain' }; }
}
function identity(value, kind) {
    if (!value || !uuid(value.internalId) || !uuid(value.versionId) || !uuid(value.productInternalId) || !positive(value.versionNumber) || !positive(value.revision) || typeof value.id !== 'string' || typeof value.title !== 'string' || !['Draft', 'InReview', 'Approved'].includes(value.status)) stop('Invalid identity.');
    return { kind, id: value.id, internalId: value.internalId, versionId: value.versionId, versionNumber: value.versionNumber,
        revision: value.revision, productInternalId: value.productInternalId, status: value.status, title: value.title,
        contentSha256: hash(Object.fromEntries(['id', 'title', 'statement', 'type', 'rationale', 'description', 'preconditions', 'steps', 'areaId'].filter(k => Object.hasOwn(value, k)).map(k => [k, value[k]]))) };
}
function compact(summary) {
    return { ...summary, operations: summary.operations?.map(({ fields, source, ...row }) => row) };
}
function sameFields(value, fields) {
    return Object.entries(fields).every(([key, expected]) => canonical(value[key]) === canonical(expected));
}
async function exclusive(path, value) {
    const handle = await open(path, 'wx', 0o600);
    try { await handle.writeFile(JSON.stringify(value, null, 2) + '\n'); await handle.sync(); } finally { await handle.close(); }
}

// Each journal line is synced before dispatch/continuation. A torn final line is treated as uncertainty.
export async function readJournal(directory) {
    const content = await readFile(join(directory, 'journal.jsonl'), 'utf8');
    const lines = content.split('\n');
    const events = [];
    for (let i = 0; i < lines.length; i++) {
        if (!lines[i]) continue;
        try { events.push(JSON.parse(lines[i])); } catch {
            if (i !== lines.length - 1) stop('Invalid journal.');
            events.push({ type: 'interrupted-checkpoint' });
        }
    }
    if (events[0]?.type !== 'start') stop('Missing journal start; inspect server state before any human-directed retry.');
    return events;
}
export function report(events) {
    const start = events[0];
    const operations = structuredClone(start.operations).map(o => ({ ...o, outcome: 'not-attempted' }));
    for (const event of events.slice(1)) {
        const row = operations[event.index];
        if (row && event.type === 'dispatch') row.outcome = 'uncertain';
        if (row && event.type === 'outcome') Object.assign(row, event.result);
    }
    return { schemaVersion: 1, phase: start.phase, manifestSha256: start.manifestSha256, productId: start.manifest.productId,
        operations, counts: Object.fromEntries(['confirmed-success', 'rejected', 'uncertain', 'not-attempted'].map(k => [k, operations.filter(o => o.outcome === k).length])) };
}

export class ImportRunner {
    constructor(call, { checkpoint, now = () => new Date().toISOString() } = {}) { this.call = call; this.checkpoint = checkpoint; this.now = now; this.requests = 0; }
    async request(name, args, signal) {
        this.requests++;
        try { return payload(await this.call(name, args, signal)); } catch { return { outcome: 'uncertain' }; }
    }
    async product(manifest, signal) {
        const response = await this.request('read_product', { productId: manifest.productId }, signal);
        if (response.outcome !== 'confirmed-success' || response.value.id !== manifest.productId || response.value.status !== 'Active') stop('Product unavailable or inactive.');
    }
    async exact(endpoint, signal) {
        if (!spec[endpoint.kind]) stop('Unsupported kind.');
        const response = await this.request(`read_${spec[endpoint.kind][0]}`, { internalId: endpoint.internalId, versionId: endpoint.versionId }, signal);
        if (response.outcome !== 'confirmed-success') stop('Exact endpoint unavailable.');
        const value = response.value[spec[endpoint.kind][2]];
        const current = identity(value, endpoint.kind);
        if (current.internalId !== endpoint.internalId || current.versionId !== endpoint.versionId || current.versionNumber !== endpoint.versionNumber) stop('Exact endpoint mismatch.');
        return { current, value };
    }
    async run(args, signal) {
        this.requests = 0;
        try {
            const allowed = {
                create: ['action', 'manifestPath', 'approvalPath', 'runDirectory', 'confirmedProduct', 'confirmed'],
                'preview-links': ['action', 'manifestPath', 'creationRunDirectory', 'proposalPath'],
                links: ['action', 'manifestPath', 'proposalPath', 'approvedSha256', 'runDirectory', 'confirmedProduct', 'confirmed'],
                reconcile: ['action', 'runDirectory', 'reportPath']
            }[args.action];
            if (!allowed || Object.keys(args).some(k => !allowed.includes(k))) stop('Unsupported operation.');
            if (args.action === 'reconcile') return await this.reconcile(args, signal);
            const manifest = validateManifest(await json(args.manifestPath));
            if (args.action === 'preview-links') return await this.previewLinks(manifest, args, signal);
            if (args.confirmed !== true || args.confirmedProduct !== manifest.productId) stop('Explicit approval and Product confirmation required.');
            const manifestSha256 = digest(manifest);
            let operations, proposal;
            if (args.action === 'create') {
                verifyApproval(manifest, await json(args.approvalPath));
                operations = manifest.dependencyOrder.map(key => {
                    const r = manifest.records.find(r => r.sourceKey === key);
                    return { sourceKey: key, source: r.source, kind: r.kind, fields: r.fields };
                });
            } else {
                proposal = await json(args.proposalPath);
                await this.validateLinks(manifest, proposal);
                if (hash(proposal) !== args.approvedSha256) stop('Changed exact endpoint proposal.');
                operations = proposal.operations;
            }
            await this.product(manifest, signal);
            // Claim once per proposal in its owning directory, independently of run directory.
            const claim = args.action === 'create' ? join(dirname(resolve(args.manifestPath)), `.retravion-create-${manifestSha256}.started`)
                : resolve(args.proposalPath) + '.started';
            await mkdir(args.runDirectory); // Never resume an existing run directory.
            await exclusive(claim, { runDirectory: resolve(args.runDirectory), message: 'Do not delete to retry. Reconcile server state and obtain human direction.' });
            const handle = await open(join(args.runDirectory, 'journal.jsonl'), 'wx', 0o600);
            const events = [];
            const save = async event => {
                const entry = { ...event, at: this.now() };
                if (this.checkpoint) await this.checkpoint(entry);
                await handle.writeFile(JSON.stringify(entry) + '\n'); await handle.sync(); events.push(entry);
            };
            const started = Date.now();
            let checkpointFailed = false, refreshFailed = false;
            try {
                await save({ type: 'start', phase: args.action, manifestSha256, manifest, operations });
                const revisions = new Map();
                for (let index = 0; index < operations.length; index++) {
                    const operation = operations[index];
                    if (signal?.aborted) break;
                    let name, parameters;
                    try {
                        if (args.action === 'create') {
                            name = `create_draft_${spec[operation.kind][0]}`;
                            parameters = { productId: manifest.productId, ...operation.fields };
                        } else {
                            const owner = (await this.exact(operation.owner, signal)).current;
                            const target = (await this.exact(operation.target, signal)).current;
                            for (const [current, approved] of [[owner, operation.owner], [target, operation.target]]) {
                                const expected = revisions.get(current.versionId) ?? approved;
                                if (canonical(current) !== canonical(expected)) stop('Stale endpoint.');
                            }
                            if (owner.status !== 'Draft' || owner.productInternalId !== target.productInternalId) stop('Ineligible owner or Product.');
                            name = 'change_draft_link';
                            parameters = { ownerKind: owner.kind, ownerId: owner.internalId, ownerVersionId: owner.versionId, expectedRevision: owner.revision,
                                relationship: operation.relationship, operation: operation.operation, targetId: target.internalId, targetVersionId: target.versionId, confirmed: true };
                        }
                    } catch {
                        await save({ type: 'outcome', index, result: { outcome: 'rejected', reason: 'Exact state unavailable, stale or ineligible. Re-preview and obtain fresh approval.' } }); break;
                    }
                    if (signal?.aborted) break;
                    await save({ type: 'dispatch', index });
                    const response = await this.request(name, parameters, signal);
                    let result = { outcome: response.outcome };
                    if (response.outcome === 'confirmed-success') {
                        if (args.action === 'create') {
                            try {
                                const created = identity(response.value, operation.kind);
                                if (!sameFields(response.value, operation.fields) || created.status !== 'Draft' || created.versionNumber !== 1) stop('Unexpected creation values.');
                                result.identity = created;
                            } catch { result = { outcome: 'uncertain', reason: 'Creation response does not confirm approved fields and exact identity. Reconcile; never recreate blindly.' }; }
                        } else {
                            const v = response.value;
                            if (v.ownerId !== parameters.ownerId || v.ownerVersionId !== parameters.ownerVersionId || v.targetId !== parameters.targetId || v.targetVersionId !== parameters.targetVersionId || v.relationship !== parameters.relationship || v.operation !== 'add')
                                result = { outcome: 'uncertain', reason: 'Link response did not confirm exact endpoints. Reconcile.' };
                        }
                    }
                    if (result.outcome !== 'confirmed-success') result.reason ??= result.outcome === 'uncertain' ? 'Outcome unknown. Retrieve current state; no automatic retry.' : 'Server rejected operation. Retrieve current state and obtain human direction.';
                    // Keep confirmed success in the compact response even if its durable checkpoint fails.
                    try { await save({ type: 'outcome', index, result }); } catch {
                        events.push({ type: 'outcome', index, result }); checkpointFailed = true; break;
                    }
                    if (result.outcome !== 'confirmed-success') break;
                    if (args.action === 'links') {
                        try {
                            const refreshed = (await this.exact(operation.owner, signal)).current;
                            const before = revisions.get(operation.owner.versionId) ?? operation.owner;
                            if (canonical(refreshed) !== canonical({ ...before, revision: before.revision + 1 })) stop('Unexpected owner state after link.');
                            revisions.set(operation.owner.versionId, refreshed);
                        }
                        catch { refreshFailed = true; break; } // Confirmed write remains confirmed; never replay after failed refresh.
                    }
                }
            } catch { checkpointFailed = true; }
            finally { try { await handle.close(); } catch { checkpointFailed = true; } }
            if (!events.length) return { error: safeError, checkpointFailed: true, requests: this.requests };
            const summary = report(events);
            summary.checkpointFailed = checkpointFailed; summary.elapsedMs = Date.now() - started; summary.requests = this.requests;
            summary.runDirectory = resolve(args.runDirectory);
            if (checkpointFailed || refreshFailed || summary.counts.uncertain || summary.counts.rejected || summary.counts['not-attempted']) summary.nextStep = 'Stop. Reconcile authorized current state before human-directed recovery; never resume or replay this run.';
            try { await exclusive(join(args.runDirectory, 'report.json'), summary); }
            catch { summary.checkpointFailed = true; summary.nextStep = 'Report write failed. Preserve this result and reconcile journal/server state; never replay.'; }
            return compact(summary);
        } catch { return { error: safeError, requests: this.requests }; }
    }
    async validateLinks(manifest, proposal) {
        exactKeys(proposal, ['schemaVersion', 'scope', 'manifestSha256', 'creationRunDirectory', 'operations']);
        if (proposal.schemaVersion !== 1 || proposal.scope !== 'add-links-only' || proposal.manifestSha256 !== digest(manifest) || !Array.isArray(proposal.operations)) stop('Invalid link proposal.');
        const creation = report(await readJournal(proposal.creationRunDirectory));
        if (creation.phase !== 'create' || creation.manifestSha256 !== digest(manifest) || creation.counts['confirmed-success'] !== manifest.records.length) stop('Unconfirmed creation mappings.');
        const mappings = new Map(creation.operations.map(o => [o.sourceKey, o.identity]));
        const matches = (actual, expected) => expected && actual.kind === expected.kind && actual.internalId === expected.internalId && actual.versionId === expected.versionId && actual.versionNumber === expected.versionNumber && actual.productInternalId === expected.productInternalId;
        const planned = manifest.records.flatMap(r => r.relationships.map(link => ({ sourceKey: r.sourceKey, relationship: link.relationship, target: link.target })));
        if (planned.length !== proposal.operations.length) stop('Changed links.');
        for (let i = 0; i < planned.length; i++) {
            const p = planned[i], o = proposal.operations[i];
            exactKeys(o, ['sourceKey', 'relationship', 'operation', 'owner', 'target']);
            if (o.operation !== 'add') stop('Only separately approved additions are supported.');
            for (const endpoint of [o.owner, o.target]) {
                exactKeys(endpoint, ['kind', 'id', 'internalId', 'versionId', 'versionNumber', 'revision', 'productInternalId', 'status', 'title', 'contentSha256']);
                if (!Object.hasOwn(spec, endpoint.kind) || typeof endpoint.contentSha256 !== 'string' || !/^[0-9a-f]{64}$/.test(endpoint.contentSha256)) stop('Invalid exact endpoint.');
            }
            if (o.sourceKey !== p.sourceKey || o.relationship !== p.relationship || o.owner.kind !== manifest.records.find(r => r.sourceKey === p.sourceKey).kind) stop('Changed link operation.');
            identity(o.owner, o.owner.kind); identity(o.target, o.target.kind);
            if (!matches(o.owner, mappings.get(p.sourceKey)) || (p.target.sourceKey && !matches(o.target, mappings.get(p.target.sourceKey)))) stop('Changed generated endpoint mapping.');
            if (o.owner.productInternalId !== o.target.productInternalId || o.owner.status !== 'Draft') stop('Invalid link owner.');
            if (!p.target.sourceKey && (o.target.kind !== p.target.kind || o.target.internalId !== p.target.internalId || o.target.versionId !== p.target.versionId || o.target.versionNumber !== p.target.versionNumber)) stop('Changed existing target.');
        }
    }
    async previewLinks(manifest, args, signal) {
        const events = await readJournal(args.creationRunDirectory), creation = report(events);
        if (creation.phase !== 'create' || creation.manifestSha256 !== digest(manifest) || creation.counts['confirmed-success'] !== manifest.records.length) stop('Creation outcomes incomplete. Reconcile first.');
        await this.product(manifest, signal);
        const mappings = new Map(creation.operations.map(o => [o.sourceKey, o.identity]));
        const operations = [];
        for (const r of manifest.records) for (const link of r.relationships) {
            const owner = (await this.exact(mappings.get(r.sourceKey), signal)).current;
            const target = (await this.exact(link.target.sourceKey ? mappings.get(link.target.sourceKey) : link.target, signal)).current;
            operations.push({ sourceKey: r.sourceKey, relationship: link.relationship, operation: 'add', owner, target });
        }
        const proposal = { schemaVersion: 1, scope: 'add-links-only', manifestSha256: digest(manifest), creationRunDirectory: resolve(args.creationRunDirectory), operations };
        await this.validateLinks(manifest, proposal);
        await exclusive(args.proposalPath, proposal);
        return { proposal, sha256: hash(proposal), requests: this.requests, nextStep: 'Show every exact owner/target endpoint and add operation. Obtain separate explicit human approval of this unchanged SHA-256 and Product before links. No mutations performed.' };
    }
    async reconcile(args, signal) {
        const events = await readJournal(args.runDirectory), summary = report(events), manifest = events[0].manifest;
        validateManifest(manifest);
        const observations = [];
        for (const operation of summary.operations) {
            if (operation.identity) {
                try { observations.push({ sourceKey: operation.sourceKey, observation: 'exact-read', identity: (await this.exact(operation.identity, signal)).current }); }
                catch { observations.push({ sourceKey: operation.sourceKey, observation: 'unavailable' }); }
            } else if (summary.phase === 'links') {
                try {
                    const current = (await this.exact(operation.owner, signal)).current;
                    const explored = await this.request('explore_relationships', { kind: current.kind, internalId: current.internalId, versionId: current.versionId, limit: 200 }, signal);
                    const view = explored.value;
                    observations.push({ sourceKey: operation.sourceKey, observation: 'exact-owner-and-links-read', identity: current,
                        incomplete: explored.outcome !== 'confirmed-success' || !Array.isArray(view?.edges) || view.truncated !== false,
                        observedEdges: Array.isArray(view?.edges) ? view.edges.filter(e => e.owner?.versionId === current.versionId && e.target?.versionId === operation.target.versionId).map(e => ({ ownerVersionId: e.owner.versionId, targetVersionId: e.target.versionId, relationship: e.relationship, isProvenance: e.isProvenance })) : [] });
                }
                catch { observations.push({ sourceKey: operation.sourceKey, observation: 'unavailable' }); }
            } else if (operation.outcome === 'uncertain') {
                const candidates = []; let offset = 0, incomplete = false;
                do {
                    if (signal?.aborted) { incomplete = true; break; }
                    const response = await this.request(`list_${spec[operation.kind][1]}`, { productId: manifest.productId, search: operation.fields.id ?? operation.fields.title.slice(0, 200), offset, limit: 50 }, signal);
                    if (response.outcome !== 'confirmed-success' || !Array.isArray(response.value.items)) { incomplete = true; break; }
                    for (const value of response.value.items) {
                        if (sameFields(value, operation.fields)) {
                            try { const endpoint = identity(value, operation.kind); candidates.push((await this.exact(endpoint, signal)).current); } catch { incomplete = true; }
                        }
                    }
                    const next = response.value.nextOffset;
                    if (signal?.aborted) { incomplete = true; break; }
                    if (next === null) break;
                    if (!Number.isSafeInteger(next) || next <= offset || next >= 200) { incomplete = true; break; }
                    offset = next;
                } while (true);
                observations.push({ sourceKey: operation.sourceKey, observation: 'candidates-only-not-proof', candidates, incomplete, ambiguous: candidates.length !== 1 || incomplete });
            }
        }
        const result = { ...summary, observations, requests: this.requests, nextStep: 'Read-only observations do not change recorded outcomes or authorize retry. Candidate identity is not proof of this operation. Resolve ambiguity with the human; obtain fresh approval for any new proposal.' };
        await exclusive(args.reportPath, result);
        return compact(result);
    }
}
