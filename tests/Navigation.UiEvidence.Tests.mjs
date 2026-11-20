// Dependency-free focused tests: node --test tests/Navigation.UiEvidence.Tests.mjs
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateSync } from 'node:zlib';
import { assertDevelopmentDom, assertDevelopmentTree, collect, contrast, developmentClip, factoryContext,
    hasDisabledText, isInside, scenarioVerdict, verify } from './Navigation.UiEvidence.mjs';

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
const keys = views.flatMap(view => ['collapsed', 'expanded'].map(state => `${view}-${state}`));
const rect = (left, top, right, bottom) => ({ left, top, right, bottom });
const row = (text, y, indent = 0) => ({ text, rect: rect(16, y, 230, y + 44),
    icon: rect(46 + indent, y + 11, 67 + indent, y + 32), label: rect(79 + indent, y + 11, 165 + indent, y + 33) });
const pngChunk = (type, data = Buffer.alloc(0)) => {
    const chunk = Buffer.alloc(12 + data.length);
    chunk.writeUInt32BE(data.length); chunk.write(type, 4); data.copy(chunk, 8);
    chunk.writeUInt32BE(crc32(chunk.subarray(4, -4)), chunk.length - 4);
    return chunk;
};
const fixtureScanlines = ({ width, height }) => Buffer.alloc((3 * width + 1) * height);
function fixturePng({ width, height }, compressed, { colorType = 2, between = [] } = {}) {
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = colorType;
    // Chromium can split the single zlib stream across multiple consecutive IDAT chunks.
    const split = Math.floor(compressed.length / 2);
    return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), pngChunk('IHDR', header),
        pngChunk('IDAT', compressed.subarray(0, split)), ...between, pngChunk('IDAT', compressed.subarray(split)), pngChunk('IEND')]);
}
function fixtureImage(clip, marker = 0) {
    const scanlines = fixtureScanlines(clip);
    scanlines[1] = marker;
    scanlines[scanlines.length - (3 * clip.width + 1)] = 4;
    return fixturePng(clip, deflateSync(scanlines));
}
const property = (name, value) => ({ name, value: { value } });
const axButton = (id, expanded) => ({ nodeId: id, role: { value: 'button' },
    name: { value: `Development for ${id} – Evidence product ${id}` }, properties: [property('expanded', expanded)] });
const axSpecifications = { nodeId: 'label', name: { value: 'Specifications' }, properties: [property('disabled', true)] };
function fixtureCapture(key) {
    const [theme, width, state] = key.split('-'), expanded = state === 'expanded';
    const capture = { product: 'UI-A', theme, width: Number(width), state };
    const nodes = [axButton('UI-A', expanded), axButton('UI-B', false), ...(expanded ? [axSpecifications] : [])];
    const geometry = { viewport: { x: 0, y: 0, width: capture.width, height: 1000 }, sidebar: rect(0, 0, 248, 1000),
        rows: [row('Revisions', 400), row('Development', 444), ...(expanded ? [row('Specifications', 488, 20)] : [])] };
    const clip = { x: 0, y: 400, width: 248, height: expanded ? 132 : 88, scale: 1 };
    const image = fixtureImage(clip, keys.indexOf(key));
    const pngSha256 = createHash('sha256').update(image).digest('hex');
    return { key, geometry, clip, image, tree: JSON.stringify({ capture: { ...capture, pngSha256 }, nodes }) };
}

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
            `UI-A native state, accessibility and crop (${view}-collapsed)`,
            `UI-A native state, accessibility and crop (${view}-expanded)`,
            `UI-B expands independently with aligned navigation (${view})`,
            `Specifications has readable theme-derived disabled styling (${view})`
        ])
    ].map(name => ({ kind: 'accessibility', name, status: 'passed' }));
    return { checks, captures: keys.map(fixtureCapture) };
}

function lateFailure() {
    const result = developmentResult();
    result.checks.splice(-2); // UI-B and contrast checks for the final viewport never ran.
    Object.assign(result.checks.at(-1), { status: 'failed', details: 'Chromium could not collect the required PNG/AX pair.' });
    result.captures.pop();
    return result;
}

function replaceImage(pair, image) {
    const tree = JSON.parse(pair.tree);
    tree.capture.pngSha256 = createHash('sha256').update(image).digest('hex');
    Object.assign(pair, { image, tree: JSON.stringify(tree) });
}

function malformedIdatCapture() {
    const result = developmentResult();
    const pair = result.captures.at(-1);
    const compressed = deflateSync(fixtureScanlines(pair.clip));
    // Legal chunk lengths and CRCs, matching dimensions and AX digest, but not a zlib stream.
    replaceImage(pair, fixturePng(pair.clip, Buffer.alloc(compressed.length)));
    return result;
}

const emptyResult = () => ({ checks: [], captures: [] });

test('aggregate requires every named assertion and the exact eight-state capture matrix', () => {
    const { checks, captures } = developmentResult();
    assert.equal(scenarioVerdict({ checks, captures }).status, 'passed');
    assert.equal(scenarioVerdict(emptyResult()).status, 'failed');
    assert.equal(scenarioVerdict({ checks: checks.slice(1), captures }).status, 'failed');
    assert.equal(scenarioVerdict({ checks: [...checks.slice(1), checks[1]], captures }).status, 'failed');
    const failedChecks = checks.map((check, i) => i === checks.length - 1 ? { ...check, status: 'failed' } : check);
    assert.equal(scenarioVerdict({ checks: failedChecks, captures }).status, 'failed');
    for (const invalid of [captures.slice(1), [...captures, captures[0]], [...captures.slice(1), captures[1]],
        [{ ...captures[0], key: 'light-1024-collapsed' }, ...captures.slice(1)]]) {
        assert.equal(scenarioVerdict({ checks, captures: invalid }).status, 'failed');
    }
});

test('capture verdict rejects swapped AX context, mismatched PNGs and incomplete PNGs', () => {
    const { checks, captures } = developmentResult();
    const [first, expanded, otherWidth] = captures;
    for (const invalid of [{ tree: expanded.tree }, { tree: otherWidth.tree }, { tree: '' },
        { image: expanded.image }, { image: otherWidth.image }, { image: first.image.subarray(0, -1) }, { image: Buffer.from('not a PNG') },
        { clip: { ...first.clip, x: 250 } }]) {
        const result = { checks, captures: [{ ...first, ...invalid }, ...captures.slice(1)] };
        assert.equal(scenarioVerdict(result).status, 'failed');
    }
});

test('capture verdict rejects malformed PNGs even with matching AX digests', () => {
    assert.equal(scenarioVerdict(malformedIdatCapture()).status, 'failed');
    const result = developmentResult(), pair = result.captures.at(-1);
    const { clip } = pair, scanlines = fixtureScanlines(clip);
    const compressed = deflateSync(scanlines);
    const invalidFilter = Buffer.from(scanlines);
    invalidFilter[scanlines.length - (3 * clip.width + 1)] = 5; // Last row's filter, not a pixel.
    const badCrc = fixturePng(clip, compressed);
    badCrc[badCrc.length - 1] ^= 1;
    for (const [label, image] of [
        ['bad CRC', badCrc],
        ['unsupported header', fixturePng(clip, compressed, { colorType: 0 })],
        ['nonconsecutive IDAT', fixturePng(clip, compressed, { between: [pngChunk('tEXt', Buffer.from('k\0v'))] })],
        ['truncated zlib stream', fixturePng(clip, compressed.subarray(0, -1))],
        ['trailing compressed bytes', fixturePng(clip, Buffer.concat([compressed, Buffer.from([1])]))],
        ['short scanlines', fixturePng(clip, deflateSync(scanlines.subarray(0, -1)))],
        ['output beyond crop size', fixturePng(clip, deflateSync(Buffer.alloc(scanlines.length + 1)))],
        ['illegal row filter', fixturePng(clip, deflateSync(invalidFilter))]
    ]) {
        replaceImage(pair, image);
        assert.equal(scenarioVerdict(result).status, 'failed', label);
    }
});

test('collapsed state requires native DOM hiding and no exposed AX Specifications', () => {
    const product = { native: true, expanded: 'false', hidden: true, groupRendered: false, specificationsRendered: false,
        nested: true, disabled: 'true', peers: ['Areas', 'User Needs', 'Requirements', 'Test Cases', 'Features', 'Revisions', 'Development']
            .map((text, index) => row(text, index * 44)) };
    assert.doesNotThrow(() => assertDevelopmentDom(product, false));
    for (const invalid of [{ native: false }, { expanded: 'true' }, { hidden: false }, { groupRendered: true },
        { specificationsRendered: true }, { nested: false }]) {
        assert.throws(() => assertDevelopmentDom({ ...product, ...invalid }, false));
    }
    const [areas, needs, ...rest] = product.peers;
    // Both are within 1px of Areas, but the unrounded Development-to-User Needs gap is > 1px.
    for (const part of ['icon', 'label']) {
        const peers = [areas, { ...needs, [part]: { left: needs[part].left + 0.6 } }, ...rest.slice(0, -1),
            { ...rest.at(-1), [part]: { left: rest.at(-1)[part].left - 0.6 } }];
        assert.throws(() => assertDevelopmentDom({ ...product, peers }, false), /align directly with User Needs/);
    }
    const visible = { ...product, expanded: 'true', hidden: false, groupRendered: true, specificationsRendered: true,
        specifications: row('Specifications', 7 * 44, 16.2) };
    assert.doesNotThrow(() => assertDevelopmentDom(visible, true));
    for (const part of ['icon', 'label']) {
        const development = rest.at(-1), peers = [...product.peers.slice(0, -1),
            { ...development, [part]: { left: development[part].left + 0.6 } }];
        assert.throws(() => assertDevelopmentDom({ ...visible, peers }, true), /deeper than Development/);
    }
    const buttons = [axButton('UI-A', false), axButton('UI-B', false)];
    assert.doesNotThrow(() => assertDevelopmentTree(buttons, 'collapsed'));
    assert.throws(() => assertDevelopmentTree([...buttons, axSpecifications], 'collapsed'), /exposed in AX/);
    const expanded = [axButton('UI-A', true), axButton('UI-B', false)];
    assert.throws(() => assertDevelopmentTree([...expanded, { ...axSpecifications, properties: [] }], 'expanded'), /disabled/);
    assert.throws(() => assertDevelopmentTree([expanded[0], axButton('UI-B', true), axSpecifications], 'expanded'), /UI-B/);
});

test('clip includes only the requested row context at native size within the visible drawer', () => {
    for (const key of keys) {
        const { geometry, clip, tree } = fixtureCapture(key), { capture } = JSON.parse(tree);
        assert.deepEqual(developmentClip(geometry, capture), clip);
        assert.deepEqual(developmentClip({ ...geometry, viewport: { ...geometry.viewport, x: 3, y: 24 } }, capture),
            { ...clip, x: 3, y: 424 });
        for (const invalid of [
            { sidebar: null }, { sidebar: rect(75, 0, 248, 1000) }, { sidebar: rect(250, 0, 350, 1000) },
            { sidebar: rect(0, 420, 248, 1000) }, { viewport: { ...geometry.viewport, height: 470 } },
            { viewport: { ...geometry.viewport, width: 300 } },
            { rows: [row('Features', 356), ...geometry.rows] }, { rows: geometry.rows.slice(1) },
            { rows: geometry.rows.map(entry => ({ ...entry, rect: { ...entry.rect, top: 695, bottom: 700 } })) },
            { rows: geometry.rows.map((entry, i) => i === 0 ? { ...entry, rect: { ...entry.rect, top: 200 } } : entry) },
            { rows: geometry.rows.map((entry, i) => i === 0 ? { ...entry, icon: rect(46, 399, 67, 432) } : entry) }
        ]) assert.throws(() => developmentClip({ ...geometry, ...invalid }, capture));
    }
});

for (const [label, result, exitCode] of [['pass', developmentResult, 0], ['late failed assertion', lateFailure, 1],
    ['empty result', emptyResult, 1], ['malformed IDAT capture with a matching AX digest', malformedIdatCapture, 1],
    ['invalid last clip with all checks passed', () => {
        const result = developmentResult(); result.captures.at(-1).clip.height = 250; return result;
    }, 1],
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
for (const [label, result] of [['complete', developmentResult], ['late failure after seven staged captures', lateFailure],
    ['missing state with all checks passed', () => { const result = developmentResult(); result.captures.pop(); return result; }],
    ['duplicate state', () => { const result = developmentResult(); result.captures[7] = result.captures[6]; return result; }],
    ['malformed IDAT with a matching AX digest', malformedIdatCapture],
    ['invalid last capture with all checks passed', () => {
        const result = developmentResult(); result.captures.at(-1).image = Buffer.alloc(0); return result;
    }]]) {
    test(`collector capture link publication for ${label}`, async () => {
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
                assert.deepEqual(manifest.imageLinks, keys.map(key => {
                    const [theme, width, state] = key.split('-');
                    return { checkName: 'navigation-ui', screenshotPath: `navigation-${key}.png`,
                        caption: `UI-A Development ${state}, ${theme} theme, ${width}px viewport — synthetic navigation crop.` };
                }));
                assert.deepEqual(manifest.artifacts, keys.flatMap(key => [
                    { kind: 'screenshot', path: `navigation-${key}.png` },
                    { kind: 'accessibility-tree', path: `navigation-${key}.accessibility.json` }
                ]));
                for (const key of keys) {
                    const { image, tree } = fixtureCapture(key);
                    assert.deepEqual(await readFile(join(output, `navigation-${key}.png`)), image);
                    assert.equal(await readFile(join(output, `navigation-${key}.accessibility.json`), 'utf8'), tree);
                }
            } else {
                assert.deepEqual(manifest.imageLinks, []);
                assert.deepEqual(manifest.artifacts, []);
            }
            assert.deepEqual((await readdir(output)).sort(), ['manifest.json', ...manifest.artifacts.map(({ path }) => path)].sort());
        } finally { await rm(output, { recursive: true, force: true }); }
    });
}

test('non-Development capture retains four generic navigation links without state claims', async () => {
    const output = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    try {
        const { checks } = developmentResult();
        const manifest = await collect({ repository, env: context(output), scenario: ({ development }) => {
            assert.equal(development, false);
            return { checks: [...checks.slice(0, 3), ...checks.filter(check => check.name.startsWith('Navigation is visible'))],
                captures: views.map(key => ({ key, image: fixtureImage({ width: Number(key.split('-')[1]), height: 1000 }),
                    tree: '{"nodes":[{}]}' })) };
        } });
        assert.deepEqual(manifest.checks.at(-1), { kind: 'interaction', name: 'product-navigation', status: 'passed' });
        assert.deepEqual(manifest.imageLinks.map(({ checkName, screenshotPath }) => [checkName, screenshotPath]),
            views.map(view => [`Navigation is visible without horizontal overflow (${view})`, `navigation-${view}.png`]));
        assert.ok(manifest.limitations.includes('Development-specific checks were not requested.'));
    } finally { await rm(output, { recursive: true, force: true }); }
});

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

test('collector capture link publication rolls back its artifacts on a write error', async () => {
    const output = await mkdtemp(join(tmpdir(), 'retravion-ui-unit-'));
    const conflict = 'navigation-dark-390-expanded.png';
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
