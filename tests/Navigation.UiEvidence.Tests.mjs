// Dependency-free focused tests: node --test tests/Navigation.UiEvidence.Tests.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { collect, contrast, factoryContext, hasDisabledText, isInside } from './Navigation.UiEvidence.mjs';

const context = directory => ({ FACTORY_TASK_ID: 'T001', FACTORY_ATTEMPT: '2',
    FACTORY_SOURCE_FINGERPRINT: 'a'.repeat(64), FACTORY_UI_EVIDENCE_DIRECTORY: directory });

test('factory context preserves task, attempt, source and destination', () => {
    const directory = resolve(tmpdir(), 'evidence');
    assert.deepEqual(factoryContext(context(directory)), {
        taskId: 'T001', attempt: 2, sourceFingerprint: 'a'.repeat(64), directory
    });
});

for (const [key, badValues] of Object.entries({
    FACTORY_TASK_ID: [undefined, '1', 'T01', 'T001\n'],
    FACTORY_ATTEMPT: [undefined, '0', '-1', '1.2', '2x', '9007199254740992'],
    FACTORY_SOURCE_FINGERPRINT: [undefined, 'a'.repeat(63), 'g'.repeat(64)],
    FACTORY_UI_EVIDENCE_DIRECTORY: [undefined, '', 'relative/evidence']
})) test(`invalid ${key} fails before starting resources`, () => {
    for (const value of badValues) assert.throws(() => factoryContext({ ...context(resolve(tmpdir())), [key]: value }));
});

test('containment distinguishes siblings with a shared prefix from children', () => {
    const root = resolve(tmpdir(), 'repo');
    assert.equal(isInside(root, root), true);
    assert.equal(isInside(root, join(root, '.factory', 'evidence')), true);
    assert.equal(isInside(root, `${root}-evidence`), false);
    assert.equal(isInside(root, resolve(root, '..', 'evidence')), false);
});

test('contrast measures rendered sRGB rather than assuming a disabled color is readable', () => {
    assert.equal(contrast([0, 0, 0], [255, 255, 255]), 21);
    assert.equal(contrast([20, 46, 67], [20, 46, 67]), 1);
    assert.equal(contrast([255, 255, 255], [0, 0, 0]), 21);
    assert.ok(contrast([135, 149, 162], [20, 46, 67]) >= 4.5);
    assert.ok(contrast([15, 35, 50], [20, 46, 67]) < 1.3);
});

test('accessibility text may inherit disabled state from its exposed container, not an unrelated node', () => {
    const disabled = { nodeId: '1', ignored: false, properties: [{ name: 'disabled', value: { value: true } }] };
    const text = { nodeId: '2', parentId: '1', ignored: false, name: { value: 'Specifications' }, properties: [] };
    assert.equal(hasDisabledText([disabled, text], 'Specifications'), true);
    assert.equal(hasDisabledText([disabled, { ...text, parentId: 'other' }], 'Specifications'), false);
    assert.equal(hasDisabledText([{ ...disabled, ignored: true }, text], 'Specifications'), false);
    assert.equal(hasDisabledText([disabled, { ...text, ignored: true }], 'Specifications'), false);
    assert.equal(hasDisabledText([disabled, text], 'Other'), false);
    assert.equal(hasDisabledText([{ ...text, parentId: '2' }], 'Specifications'), false);
});

test('collector rejects in-worktree evidence before launching browser, database or app', async () => {
    const root = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        await writeFile(join(root, 'Retravion.slnx'), 'fixture');
        await assert.rejects(collect({ repository: root, env: context(join(root, 'evidence')) }), /outside the target worktree/);
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('collector rejects a nonempty evidence directory to prevent stale artifacts', async () => {
    const root = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        const repo = join(root, 'repo'), output = join(root, 'evidence');
        await mkdir(repo); await mkdir(output);
        await writeFile(join(repo, 'Retravion.slnx'), 'fixture');
        await writeFile(join(output, 'old.png'), 'stale');
        await assert.rejects(collect({ repository: repo, env: context(output) }), /fresh, empty/);
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('missing browser is a prerequisite error, not fabricated passing evidence', async () => {
    const root = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        const repo = join(root, 'repo'); await mkdir(repo);
        await writeFile(join(repo, 'Retravion.slnx'), 'fixture');
        await assert.rejects(collect({ repository: repo, env: { ...context(join(root, 'evidence')),
            RETRAVION_TEST_BROWSER: join(root, 'missing-browser.exe') } }), /installed Chromium executable/);
    } finally { await rm(root, { recursive: true, force: true }); }
});
