// Dependency-free focused tests: node --test tests/Navigation.UiEvidence.Tests.mjs
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { collect, contrast, factoryContext, hasDisabledText, isInside, scenarioVerdict, verify } from './Navigation.UiEvidence.mjs';

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

test('collector refuses link publication inside the worktree before launching any services', async () => {
    const root = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        await writeFile(join(root, 'Retravion.slnx'), 'fixture');
        const output = join(root, 'evidence');
        await mkdir(output);
        await assert.rejects(collect({ repository: root, env: context(output) }), /outside the target worktree/);
        assert.deepEqual(await readdir(output), []);
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

test('verify fails for a missing browser rather than fabricating evidence', async () => {
    const root = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        await writeFile(join(root, 'Retravion.slnx'), 'fixture');
        await assert.rejects(verify({ repository: root, env: { RETRAVION_TEST_BROWSER: join(root, 'missing-browser.exe') } }),
            /installed Chromium executable/);
    } finally { await rm(root, { recursive: true, force: true }); }
});

const views = ['light-1280', 'light-390', 'dark-1280', 'dark-390'];
const fixtureImage = view => Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), Buffer.from(view)]);
function developmentResult() {
    const checks = [
        'Two isolated products appear in product navigation',
        'Product mouse toggle is independent and preserves links and location',
        'Product keyboard toggle updates native expanded state and visibility',
        'Development starts collapsed for both products',
        'Mouse toggles only the targeted Development node and preserves navigation',
        'Enter and Space toggle the native button with matching accessible state',
        'Specifications is disabled, not a link or focus stop, and does not navigate',
        ...views.flatMap(view => [
            `Navigation is visible without horizontal overflow (${view})`,
            `Specifications has readable theme-derived disabled styling (${view})`,
            `Accessibility tree exposes expanded Development and disabled Specifications (${view})`
        ])
    ].map(name => ({ kind: 'accessibility', name, status: 'passed' }));
    const captures = new Map(views.map(view => [view, { image: fixtureImage(view), tree: JSON.stringify({ nodes: [view] }) }]));
    return { checks, captures };
}

function lateFailure() {
    const result = developmentResult();
    Object.assign(result.checks.at(-1), { status: 'failed', details: 'Disabled semantics not exposed.' });
    result.captures.delete('dark-390');
    return result;
}

test('aggregate requires every named assertion and all four capture pairs', () => {
    assert.equal(scenarioVerdict(developmentResult()).status, 'passed');
    assert.equal(scenarioVerdict({ checks: [], captures: new Map() }).status, 'failed');
    const { checks, captures } = developmentResult();
    assert.equal(scenarioVerdict({ checks: checks.slice(1), captures }).status, 'failed');
    assert.equal(scenarioVerdict({ checks: [...checks.slice(1), checks[1]], captures }).status, 'failed');
    assert.equal(scenarioVerdict({ checks: lateFailure().checks, captures }).status, 'failed');
    captures.set('dark-390', { image: fixtureImage('dark-390') });
    assert.equal(scenarioVerdict({ checks, captures }).status, 'failed');
    captures.set('dark-390', { image: Buffer.from('not a PNG'), tree: '{"nodes":[{}]}' });
    assert.equal(scenarioVerdict({ checks, captures }).status, 'failed');
});

for (const [label, result, exitCode] of [['pass', developmentResult, 0], ['late failed assertion', lateFailure, 1],
    ['empty result', () => ({ checks: [], captures: new Map() }), 1],
    ['operational error', () => { throw new Error('startup failed'); }, null]]) {
    test(`verify cleanup after ${label}, without factory identity or publication`, async () => {
        const repo = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
        let scratchPath;
        const disposed = [];
        try {
            await writeFile(join(repo, 'Retravion.slnx'), 'fixture');
            const run = verify({ repository: repo, env: {}, async scenario({ env, development, scratch, own }) {
                assert.deepEqual(env, {});
                assert.equal(development, true);
                scratchPath = scratch;
                assert.equal(isInside(repo, scratch), false);
                for (const resource of ['database', 'app', 'browser']) {
                    const file = join(scratch, resource);
                    own(resource, async () => { await rm(file); disposed.push(resource); });
                    await writeFile(file, 'owned');
                }
                await writeFile(join(scratch, 'temporary-artifact.png'), 'staged');
                return result();
            } });
            if (exitCode === null) await assert.rejects(run, /startup failed/);
            else assert.equal(await run, exitCode);
            assert.deepEqual(disposed, ['browser', 'app', 'database']);
            await assert.rejects(readdir(scratchPath), { code: 'ENOENT' });
            assert.deepEqual(await readdir(repo), ['Retravion.slnx']);
        } finally { await rm(repo, { recursive: true, force: true }); }
    });
}

const repository = fileURLToPath(new URL('../', import.meta.url));
for (const [label, result] of [['complete', developmentResult], ['late failure after three staged captures', lateFailure]]) {
    test(`collector link publication for ${label}`, async () => {
        const output = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
        try {
            const manifest = await collect({ repository, env: context(output), development: true, scenario: result });
            const persisted = JSON.parse(await readFile(join(output, 'manifest.json'), 'utf8'));
            assert.deepEqual(persisted, manifest);
            assert.equal(manifest.taskId, 'T001');
            assert.equal(manifest.attempt, 2);
            assert.equal(manifest.sourceFingerprint, 'a'.repeat(64));
            assert.match(manifest.application.buildId, /^[a-f0-9]+:a{64}$/);
            assert.deepEqual(manifest.checks.filter(check => check.name !== 'navigation-ui'), result().checks);
            const aggregate = manifest.checks.filter(check => check.name === 'navigation-ui');
            assert.equal(aggregate.length, 1);
            assert.equal(aggregate[0].status, label === 'complete' ? 'passed' : 'failed');
            if (label === 'complete') {
                assert.deepEqual(manifest.imageLinks.map(({ checkName, screenshotPath }) => [checkName, screenshotPath]),
                    views.map(view => ['navigation-ui', `navigation-${view}.png`]));
                assert.deepEqual(manifest.artifacts, views.flatMap(view => [
                    { kind: 'screenshot', path: `navigation-${view}.png` },
                    { kind: 'accessibility-tree', path: `navigation-${view}.accessibility.json` }
                ]));
                for (const view of views) {
                    assert.deepEqual(await readFile(join(output, `navigation-${view}.png`)), fixtureImage(view));
                    assert.deepEqual(JSON.parse(await readFile(join(output, `navigation-${view}.accessibility.json`), 'utf8')), { nodes: [view] });
                }
            } else {
                assert.deepEqual(manifest.imageLinks, []);
                assert.deepEqual(manifest.artifacts, []);
            }
            assert.deepEqual((await readdir(output)).sort(), ['manifest.json', ...manifest.artifacts.map(({ path }) => path)].sort());
        } finally { await rm(output, { recursive: true, force: true }); }
    });
}

test('verify CLI returns nonzero for a prerequisite failure without FACTORY_* context', async () => {
    const repo = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        await writeFile(join(repo, 'Retravion.slnx'), 'fixture');
        const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^FACTORY_/i.test(key)));
        const result = spawnSync(process.execPath, [join(repository, 'tests/Navigation.UiEvidence.mjs'), '--development', '--verify'],
            { cwd: repo, env: { ...env, RETRAVION_TEST_BROWSER: join(repo, 'missing-browser.exe') }, encoding: 'utf8', timeout: 10_000 });
        assert.ifError(result.error);
        assert.equal(result.status, 1);
        assert.match(result.stderr, /installed Chromium executable/);
        assert.deepEqual(await readdir(repo), ['Retravion.slnx']);
    } finally { await rm(repo, { recursive: true, force: true }); }
});

test('collector link publication rolls back its artifacts on a write error', async () => {
    const output = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    const conflict = 'navigation-dark-390.png';
    try {
        await assert.rejects(collect({ repository, env: context(output), development: true, async scenario() {
            // Introduce a conflicting file after collection has checked that the directory is empty.
            await writeFile(join(output, conflict), 'not owned by the collector');
            return developmentResult();
        } }), { code: 'EEXIST' });
        assert.deepEqual(await readdir(output), [conflict]);
        assert.equal(await readFile(join(output, conflict), 'utf8'), 'not owned by the collector');
    } finally { await rm(output, { recursive: true, force: true }); }
});

test('collector cannot publish links or a manifest after a cleanup error', async () => {
    const output = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        let scratchPath;
        await assert.rejects(collect({ repository, env: context(output), development: true, scenario({ scratch, own }) {
            scratchPath = scratch;
            own('browser', () => { throw new Error('stop failed'); });
            return developmentResult();
        } }), /Cleanup failed \(browser\)/);
        await assert.rejects(readdir(scratchPath), { code: 'ENOENT' });
        assert.deepEqual(await readdir(output), []);
    } finally { await rm(output, { recursive: true, force: true }); }
});
