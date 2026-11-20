// Standalone navigation verification and opt-in pi-factory collection. Run from the target repository root.
// Node 24+, .NET SDK from global.json, Podman and Edge/Chrome; no npm packages.
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawn, execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, open, readFile, readdir, realpath, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { crc32, inflateSync } from 'node:zlib';

const exec = promisify(execFile);
const delay = ms => new Promise(done => setTimeout(done, ms));
const aggregateName = 'navigation-ui';
const productChecks = [
    'Two isolated products appear in product navigation',
    'Product mouse toggle is independent and preserves links and location',
    'Product keyboard toggle updates native expanded state and visibility'
];
const developmentChecks = [
    'Development starts collapsed for both products',
    'Mouse toggles only the targeted Development node and preserves navigation',
    'Enter and Space toggle the native button with matching accessible state',
    'Specifications is disabled, not a link or focus stop, and does not navigate'
];
const viewports = ['light-1280', 'light-390', 'dark-1280', 'dark-390'];
const pngSignature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const layoutCheck = name => `Navigation is visible without horizontal overflow (${name})`;
const styleCheck = name => `Specifications has readable theme-derived disabled styling (${name})`;
const states = ['collapsed', 'expanded'];
const stateKey = (view, state) => `${view}-${state}`;
const imageHash = image => createHash('sha256').update(image).digest('hex');
const developmentCaptures = viewports.flatMap(view => states.map(state => stateKey(view, state)));
const captureCheck = key => `UI-A native state, accessibility and crop (${key})`;
const peerCheck = view => `UI-B expands independently with aligned navigation (${view})`;
const captureContext = (view, state) => {
    const [theme, width] = view.split('-');
    return { product: 'UI-A', state, theme, width: Number(width) };
};

export function developmentClip({ viewport, sidebar, rows }, { width, state }) {
    const positiveRect = rect => rect && [rect.left, rect.top, rect.right, rect.bottom].every(Number.isFinite) &&
        rect.right > rect.left && rect.bottom > rect.top;
    assert.ok(viewport.width === width && Number.isFinite(viewport.height) && viewport.height > 0 &&
        [viewport.x, viewport.y].every(value => Number.isInteger(value) && value >= 0), 'Unexpected capture viewport.');
    assert.ok(positiveRect(sidebar), 'Sidebar is not visible.');
    assert.ok(states.includes(state), 'Unexpected capture state.');
    const expected = ['Revisions', 'Development', ...(state === 'expanded' ? ['Specifications'] : [])];
    assert.deepEqual(rows.map(row => row.text), expected, 'Unexpected rows in the Development crop.');
    // Round inward so an adjacent row or the workspace cannot bleed into the crop.
    const left = Math.ceil(Math.max(sidebar.left, 0)), right = Math.floor(Math.min(sidebar.right, viewport.width));
    const top = Math.ceil(rows[0].rect.top), bottom = Math.floor(rows.at(-1).rect.bottom);
    assert.ok(right > left && bottom > top && bottom - top < 250 &&
        rows[0].rect.top >= Math.max(0, sidebar.top) && rows.at(-1).rect.bottom <= Math.min(viewport.height, sidebar.bottom),
        'Crop must be positive, shorter than 250px, and within the visible sidebar and viewport.');
    for (const [index, row] of rows.entries()) {
        assert.ok(positiveRect(row.rect) && row.rect.left >= left && row.rect.right <= right &&
            (!index || Math.abs(row.rect.top - rows[index - 1].rect.bottom) < 1), 'Crop rows must be fully visible and adjacent.');
        for (const content of [row.icon, row.label]) {
            assert.ok(positiveRect(content) && content.left >= row.rect.left && content.right <= row.rect.right &&
                content.top >= Math.max(top, row.rect.top) && content.bottom <= Math.min(bottom, row.rect.bottom),
                'Crop must contain each complete icon and label within its row.');
        }
    }
    // DOM rectangles are viewport-relative; Chromium's clip uses CSS page coordinates.
    return { x: viewport.x + left, y: viewport.y + top, width: right - left, height: bottom - top, scale: 1 };
}

function validPng(image, clip) {
    if (!Buffer.isBuffer(image) || image.length <= pngSignature.length || !image.subarray(0, 8).equals(pngSignature)) return false;
    if (!clip) return true; // Preserve the non-Development full-viewport evidence contract.
    // The opaque Chromium crops are 8-bit RGB, non-interlaced, with only IHDR, IDAT(s), IEND.
    const scanlineBytes = 1 + 3 * clip.width; // One filter byte followed by RGB samples.
    const expectedBytes = scanlineBytes * clip.height;
    const data = [];
    for (let offset = pngSignature.length; offset + 12 <= image.length;) {
        const size = image.readUInt32BE(offset), end = offset + 12 + size;
        if (end > image.length || crc32(image.subarray(offset + 4, end - 4)) !== image.readUInt32BE(end - 4)) return false;
        const type = image.toString('latin1', offset + 4, offset + 8);
        const payload = image.subarray(offset + 8, end - 4);
        if (offset === pngSignature.length) {
            if (type !== 'IHDR' || size !== 13 || payload.readUInt32BE(0) !== clip.width ||
                payload.readUInt32BE(4) !== clip.height || !payload.subarray(8).equals(Buffer.from([8, 2, 0, 0, 0]))) return false;
        } else if (type === 'IDAT') {
            data.push(payload);
        } else if (type === 'IEND' && size === 0 && end === image.length && data.length) {
            const compressed = Buffer.concat(data);
            try {
                const { buffer, engine } = inflateSync(compressed, { maxOutputLength: expectedBytes, info: true });
                if (buffer.length !== expectedBytes || engine.bytesWritten !== compressed.length) return false;
                for (let row = 0; row < buffer.length; row += scanlineBytes) if (buffer[row] > 4) return false;
                return true;
            } catch { return false; }
        } else return false;
        offset = end;
    }
    return false;
}

export function scenarioVerdict({ checks, captures }, development = true) {
    const required = [...productChecks, ...(development ? developmentChecks : []),
        ...viewports.flatMap(view => development ? [layoutCheck(view), styleCheck(view), peerCheck(view),
            ...states.map(state => captureCheck(stateKey(view, state)))] : [layoutCheck(view)])];
    const expected = development ? developmentCaptures : viewports;
    const passed = Array.isArray(checks) && checks.length === required.length && required.every(name =>
        checks.some(check => check?.name === name && check.status === 'passed')) &&
        Array.isArray(captures) && captures.length === expected.length && expected.every(key => {
            const pairs = captures.filter(pair => pair?.key === key);
            if (pairs.length !== 1) return false;
            const [pair] = pairs;
            try {
                const tree = JSON.parse(pair.tree);
                if (!Array.isArray(tree.nodes) || !tree.nodes.length) return false;
                if (!development) return validPng(pair.image);
                const [theme, width, state] = key.split('-');
                const context = captureContext(`${theme}-${width}`, state);
                assert.deepEqual(pair.clip, developmentClip(pair.geometry, context));
                if (!validPng(pair.image, pair.clip)) return false;
                assert.deepEqual(tree.capture, { ...context, pngSha256: imageHash(pair.image) });
                assertDevelopmentTree(tree.nodes, state);
                return true;
            } catch { return false; }
        });
    return { kind: 'interaction', name: development ? aggregateName : 'product-navigation', status: passed ? 'passed' : 'failed',
        ...(passed ? {} : { details: `The scenario did not pass every required check and complete all ${expected.length} distinct capture pairs.` }) };
}

const limitations = [
    'Scoped to product navigation, not other changed pages or comprehensive accessibility compliance.',
    'Accessibility-tree and native keyboard checks do not replace assistive-technology user testing.',
    'Forced termination can prevent cleanup; inspect owned resources after interruption.'
];

export function factoryContext(env) {
    assert.match(env.FACTORY_TASK_ID ?? '', /^T[0-9]{3,}$/, 'Missing or invalid FACTORY_TASK_ID.');
    assert.match(env.FACTORY_ATTEMPT ?? '', /^[1-9][0-9]*$/, 'Missing or invalid FACTORY_ATTEMPT.');
    const attempt = Number(env.FACTORY_ATTEMPT);
    assert.ok(Number.isSafeInteger(attempt), 'Invalid FACTORY_ATTEMPT.');
    assert.match(env.FACTORY_SOURCE_FINGERPRINT ?? '', /^[a-f0-9]{64}$/, 'Missing or invalid FACTORY_SOURCE_FINGERPRINT.');
    assert.ok(isAbsolute(env.FACTORY_UI_EVIDENCE_DIRECTORY ?? ''), 'Evidence directory must be absolute.');
    return { taskId: env.FACTORY_TASK_ID, attempt, sourceFingerprint: env.FACTORY_SOURCE_FINGERPRINT,
        directory: env.FACTORY_UI_EVIDENCE_DIRECTORY };
}

export function isInside(root, candidate) {
    const path = relative(root, candidate);
    return path === '' || (!isAbsolute(path) && path !== '..' && !path.startsWith(`..${sep}`));
}

export function contrast(first, second) {
    const luminance = rgb => rgb.map(value => {
        const channel = value / 255;
        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
    }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
    const a = luminance(first), b = luminance(second);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

export function hasDisabledText(nodes, text) {
    const byId = new Map(nodes.map(node => [node.nodeId, node]));
    return nodes.some(node => {
        if (node.ignored || node.name?.value !== text) return false;
        const visited = new Set();
        for (let current = node; current && !visited.has(current.nodeId); current = byId.get(current.parentId)) {
            visited.add(current.nodeId);
            if (!current.ignored && current.properties?.some(property => property.name === 'disabled' && property.value.value === true)) return true;
        }
        return false;
    });
}

export function assertDevelopmentTree(nodes, state) {
    const expanded = state === 'expanded';
    for (const [id, expected] of [['UI-A', expanded], ['UI-B', false]]) {
        const buttons = nodes.filter(node => !node.ignored && node.role?.value === 'button' &&
            node.name?.value === `Development for ${id} – Evidence product ${id}`);
        assert.equal(buttons.length, 1, `Expected one exposed Development button for ${id}.`);
        assert.ok(buttons[0].properties?.some(property => property.name === 'expanded' && property.value.value === expected),
            `Incorrect AX expanded state for ${id}.`);
    }
    if (expanded) assert.ok(hasDisabledText(nodes, 'Specifications'), 'Specifications must belong to an exposed disabled node.');
    else assert.ok(!nodes.some(node => !node.ignored && node.name?.value === 'Specifications'), 'Collapsed Specifications is exposed in AX.');
}

const childGroup = id => `#product-navigation-children-${id}`;
const developmentButton = id => `${childGroup(id)} > button[aria-controls^="product-navigation-development-"]`;

// Runs in Chromium; use rendered boxes (not rounded offsets) and do not mutate the DOM.
function inspectDevelopment(selector) {
    const button = document.querySelector(selector), children = button.parentElement;
    const revisions = children.querySelector(':scope > a[href^="/revisions?"]');
    const group = document.getElementById(button.getAttribute('aria-controls'));
    const specifications = group.querySelector(':scope > .product-navigation-specifications');
    const sidebar = document.querySelector('#app-navigation');
    const rect = e => e.getBoundingClientRect().toJSON();
    const rendered = e => e.getClientRects().length > 0 && e.checkVisibility({ checkVisibilityCSS: true, checkOpacity: true });
    const row = e => ({ text: e.textContent.trim(), rect: rect(e), icon: rect(e.querySelector('svg')),
        label: rect(e.querySelector('span')) });
    const peers = [...children.querySelectorAll(':scope > .product-navigation-child')].map(row);
    const side = rect(sidebar), viewport = window.visualViewport;
    const visibleRows = [revisions, button, ...(rendered(specifications) ? [specifications] : [])];
    return {
        expanded: button.getAttribute('aria-expanded'), hidden: group.hidden,
        native: button instanceof HTMLButtonElement && button.type === 'button' && !button.disabled,
        nested: button.nextElementSibling === group && !revisions.contains(specifications),
        groupRendered: rendered(group), specificationsRendered: rendered(specifications),
        disabled: specifications.getAttribute('aria-disabled'),
        peers, specifications: rendered(specifications) ? row(specifications) : null,
        geometry: {
            viewport: { x: viewport.pageLeft, y: viewport.pageTop, width: innerWidth, height: innerHeight },
            sidebar: rendered(sidebar) ? { left: side.left + sidebar.clientLeft, top: side.top + sidebar.clientTop,
                right: side.left + sidebar.clientLeft + sidebar.clientWidth, bottom: side.top + sidebar.clientTop + sidebar.clientHeight } : null,
            rows: visibleRows.map(row)
        },
        unobscured: viewport.scale === 1 && visibleRows.every(e => {
            const r = rect(e), hit = document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2);
            return rendered(e) && sidebar.contains(e) && e.contains(hit);
        })
    };
}

export function assertDevelopmentDom(product, expanded) {
    assert.equal(product.native, true, 'Development must be an enabled native button.');
    assert.equal(product.expanded, String(expanded), 'Unexpected DOM aria-expanded state.');
    assert.equal(product.hidden, !expanded, 'Unexpected controlled group hidden state.');
    assert.equal(product.groupRendered, expanded, 'Unexpected controlled group rendering.');
    assert.equal(product.specificationsRendered, expanded, 'Unexpected Specifications rendering.');
    assert.equal(product.nested, true, 'Specifications must be nested inside Development, not Revisions.');
    assert.equal(product.disabled, 'true');
    assert.deepEqual(product.peers.map(row => row.text), ['Areas', 'User Needs', 'Requirements', 'Test Cases', 'Features', 'Revisions', 'Development']);
    const [areas] = product.peers, development = product.peers.at(-1);
    for (const part of ['icon', 'label']) {
        for (const peer of product.peers.slice(1)) {
            assert.ok(Math.abs(peer[part].left - areas[part].left) <= 1, `${peer.text} ${part} must align with Areas.`);
        }
        for (const peer of product.peers.slice(0, -1)) {
            assert.ok(Math.abs(development[part].left - peer[part].left) <= 1, `Development ${part} must align directly with ${peer.text}.`);
        }
        if (expanded) {
            assert.ok(product.specifications[part].left >= areas[part].left + 16, `Specifications ${part} must remain deeper than Areas.`);
            assert.ok(product.specifications[part].left >= development[part].left + 16, `Specifications ${part} must be deeper than Development.`);
        }
    }
}

// Never propagate native output/arguments: bootstrap commands may contain sensitive configuration.
async function command(executable, args, options = {}) {
    try { return (await exec(executable, args, { timeout: 60_000, maxBuffer: 2 * 1024 * 1024, windowsHide: true, ...options })).stdout.trim(); }
    catch { throw new Error(`${executable === 'dotnet' ? '.NET' : executable === 'podman' ? 'Podman' : 'External'} command failed.`); }
}

async function waitFor(test, description, timeout = 20_000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        if (await test()) return;
        await delay(100);
    }
    throw new Error(`Timed out: ${description}.`);
}

async function stop(child) {
    if (!child?.pid || child.exitCode !== null || child.signalCode !== null) return;
    const closed = new Promise(done => child.once('close', done));
    if (process.platform === 'win32') {
        try { await command('taskkill', ['/PID', String(child.pid), '/T', '/F']); }
        catch {
            // Browser.close can complete between the liveness check and taskkill.
            await Promise.race([closed, delay(500)]);
            assert.ok(child.exitCode !== null || child.signalCode !== null, 'Owned process could not be stopped.');
        }
    } else child.kill('SIGTERM');
    await Promise.race([closed, delay(5000)]);
    assert.ok(child.exitCode !== null || child.signalCode !== null, 'Owned process did not stop.');
}

class BrowserProtocolError extends Error {}

async function openBrowser(executable, profile) {
    const child = spawn(executable, ['--headless=new', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0',
        `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check', '--no-proxy-server', 'about:blank'],
    { stdio: 'ignore', windowsHide: true });
    let startupError = false;
    child.on('error', () => { startupError = true; });
    let socket;
    try {
        let port;
        await waitFor(async () => {
            assert.ok(!startupError && child.exitCode === null, 'Browser did not start.');
            try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0].trim(); return /^\d+$/.test(port); }
            catch { return false; }
        }, 'browser debugging port');
        const response = await fetch(`http://127.0.0.1:${port}/json/new?about:blank`,
            { method: 'PUT', redirect: 'error', signal: AbortSignal.timeout(5000) });
        assert.ok(response.ok, 'Browser target unavailable.');
        const target = await response.json();
        const socketUrl = new URL(target.webSocketDebuggerUrl);
        assert.ok(socketUrl.origin === `ws://127.0.0.1:${port}`, 'Unexpected browser debugging endpoint.');
        socket = new WebSocket(socketUrl);
        let nextId = 0, connectedFrames = 0;
        const pending = new Map(), transports = new Set();
        socket.addEventListener('message', ({ data }) => {
            const message = JSON.parse(data);
            if (message.method === 'Network.webSocketCreated' && new URL(message.params.url).pathname.endsWith('/_blazor')) transports.add(message.params.requestId);
            if (message.method === 'Network.webSocketFrameReceived' && transports.has(message.params.requestId)) connectedFrames++;
            const item = pending.get(message.id);
            if (!item) return;
            pending.delete(message.id); clearTimeout(item.timer);
            if (message.error) item.reject(new BrowserProtocolError('Browser protocol command failed.')); else item.resolve(message.result);
        });
        socket.addEventListener('close', () => {
            for (const item of pending.values()) { clearTimeout(item.timer); item.reject(new BrowserProtocolError('Browser connection closed.')); }
            pending.clear();
        });
        await new Promise((done, reject) => {
            const timer = setTimeout(() => reject(new Error('Browser connection timed out.')), 10_000);
            socket.addEventListener('open', () => { clearTimeout(timer); done(); }, { once: true });
            socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('Browser connection failed.')); }, { once: true });
        });
        const send = (method, params = {}) => new Promise((done, reject) => {
            const id = ++nextId;
            if (socket.readyState !== WebSocket.OPEN) { reject(new BrowserProtocolError('Browser connection closed.')); return; }
            const timer = setTimeout(() => { pending.delete(id); reject(new BrowserProtocolError('Browser protocol command timed out.')); }, 15_000);
            pending.set(id, { resolve: done, reject, timer });
            try { socket.send(JSON.stringify({ id, method, params })); }
            catch { pending.delete(id); clearTimeout(timer); reject(new BrowserProtocolError('Browser protocol send failed.')); }
        });
        const evaluate = async expression => {
            const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
            assert.ok(!result.exceptionDetails, 'Browser evaluation failed.');
            return result.result.value;
        };
        await send('Page.enable'); await send('Page.bringToFront'); await send('Network.enable'); await send('Accessibility.enable');
        return { child, socket, send, evaluate, get connectedFrames() { return connectedFrames; } };
    } catch (error) { socket?.close(); await stop(child); throw error; }
}

async function repositoryRoot(repository) {
    const repo = await realpath(repository);
    assert.ok(existsSync(join(repo, 'Retravion.slnx')), 'Run from a Retravion repository root.');
    return repo;
}

async function withScenario({ repo, env, development, scenario }) {
    const tempRoot = await realpath(tmpdir());
    assert.ok(!isInside(repo, tempRoot), 'Temporary files must be outside the target worktree.');
    const scratch = await mkdtemp(join(tempRoot, 'retravion-ui-scenario-'));
    const owned = [];
    const own = (name, dispose) => owned.push({ name, dispose });
    own('temporary files', () => rm(scratch, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }));
    let interrupted = false;
    const onSignal = () => { interrupted = true; };
    const checkpoint = () => { if (interrupted) throw new Error('Navigation scenario interrupted.'); };
    process.on('SIGINT', onSignal); process.on('SIGTERM', onSignal);
    try {
        return await scenario({ repo, env, development, scratch, own, checkpoint });
    } finally {
        const failures = [];
        try {
            for (const { name, dispose } of owned.reverse()) {
                try { await dispose(); } catch { failures.push(name); }
            }
        } finally {
            process.removeListener('SIGINT', onSignal); process.removeListener('SIGTERM', onSignal);
        }
        if (failures.length) throw new Error(`Cleanup failed (${failures.join(', ')}).`);
        checkpoint();
    }
}

class ScenarioAssertionFailure extends Error {}

async function runScenario({ env, repo, development, scratch, own, checkpoint }) {
    const executable = env.RETRAVION_TEST_BROWSER || [
        'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
        'C:/Program Files/Google/Chrome/Application/chrome.exe'
    ].find(existsSync);
    assert.ok(executable && existsSync(executable), 'Set RETRAVION_TEST_BROWSER to an installed Chromium executable.');
    const id = randomUUID().replaceAll('-', '');
    const container = `retravion-ui-evidence-${id}`;
    const username = `ui-${id.slice(0, 12)}`;
    const password = () => `Aa1!${randomUUID().replaceAll('-', '').slice(0, 20)}`;
    const databasePassword = password(), initialPassword = password(), replacementPassword = password();
    const profile = join(scratch, 'browser');
    const build = join(scratch, 'build');
    const checks = [], captures = [];
    const check = async (kind, name, action) => {
        checkpoint();
        try { await action(); checks.push({ kind, name, status: 'passed' }); }
        catch (error) {
            if (error instanceof BrowserProtocolError) throw error;
            // Assertions below contain only fixed expectations and synthetic navigation data, never credentials.
            checks.push({ kind, name, status: 'failed', details: error.message.slice(0, 1000) });
            console.log(`Navigation assertion failed: ${name}: ${error.message.slice(0, 1000)}`);
            throw new ScenarioAssertionFailure();
        }
    };
    try {
        console.log(`Starting isolated navigation environment (${container}).`);
        const tempEnv = { TMP: scratch, TEMP: scratch, TMPDIR: scratch };
        await command('dotnet', ['build', 'src/Retravion.Web/Retravion.Web.csproj', '--configuration', 'Release',
            '--disable-build-servers', '--artifacts-path', build], { cwd: repo, env: { ...env, ...tempEnv }, timeout: 180_000 });
        checkpoint();
        const podmanEnv = { ...env };
        for (const key of Object.keys(podmanEnv)) {
            if (/^(CONTAINER_HOST|CONTAINER_CONNECTION|DOCKER_HOST)$/i.test(key)) delete podmanEnv[key];
        }
        const podman = (args, options = {}) => command('podman', args, { env: podmanEnv, ...options });
        // The name is ours even if Podman loses the create acknowledgement.
        own(`database ${container}`, () => podman(['rm', '--force', '--ignore', '--volumes', container]));
        await podman(['run', '--detach', '--rm', '--name', container, '--env', 'POSTGRES_PASSWORD', '--publish', '127.0.0.1::5432', 'postgres:17'],
            { env: { ...podmanEnv, POSTGRES_PASSWORD: databasePassword } });
        await waitFor(async () => {
            checkpoint();
            try { await podman(['exec', container, 'pg_isready', '-U', 'postgres'], { timeout: 5000 }); return true; }
            catch { return false; }
        }, 'disposable PostgreSQL readiness', 60_000);
        const binding = await podman(['port', container, '5432/tcp']);
        assert.match(binding, /^127\.0\.0\.1:\d+$/, 'Unexpected PostgreSQL port binding.');
        const connection = `Host=127.0.0.1;Port=${binding.split(':')[1]};Database=postgres;Username=postgres;Password=${databasePassword}`;
        const environment = development ? 'Development' : 'Production';
        // Only inherit OS/runtime discovery settings, not external app, hosting or database configuration.
        const systemEnv = Object.fromEntries(Object.entries(env).filter(([key]) =>
            /^(PATH|SystemRoot|SystemDrive|WINDIR|ProgramData|ProgramFiles(\(x86\))?|DOTNET_ROOT(_(X64|ARM64|X86))?|LANG)$/i.test(key)));
        // Keep application profile files (including cookie-protection keys) with the disposable app.
        const appEnv = { ...systemEnv, ...tempEnv, HOME: scratch, USERPROFILE: scratch, LOCALAPPDATA: scratch, APPDATA: scratch,
            DOTNET_ENVIRONMENT: environment, ASPNETCORE_ENVIRONMENT: environment,
            ConnectionStrings__DefaultConnection: connection, InitialAdmin__Username: username, InitialAdmin__Password: initialPassword,
            Logging__LogLevel__Default: 'Warning', 'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' };
        checkpoint();
        // Launch the built app directly. Use the built static asset manifests and source webroot,
        // but don't load local appsettings overrides from the checkout.
        const app = spawn('dotnet', [join(build, 'bin/Retravion.Web/release/Retravion.Web.dll'),
            '--contentRoot', scratch, '--webroot', join(repo, 'src/Retravion.Web/wwwroot'), '--urls', 'http://127.0.0.1:0'],
        { cwd: scratch, env: appEnv, stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true });
        own('application', () => stop(app));
        let address, startupError = false, startupOutput = '';
        app.on('error', () => { startupError = true; });
        app.stdout.on('data', bytes => {
            if (address) return; // Drain stdout without buffering it after obtaining the loopback address.
            startupOutput = (startupOutput + bytes.toString()).slice(-8192);
            address = startupOutput.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)\s/)?.[1];
        });
        await waitFor(() => {
            checkpoint();
            assert.ok(!startupError && app.exitCode === null, 'Application did not start.');
            return address;
        }, 'isolated application readiness', 60_000);
        startupOutput = '';
        const browser = await openBrowser(executable, profile);
        own('browser', async () => {
            try { await browser.send('Browser.close'); } catch { /* The protocol may close before its reply. */ }
            try { browser.socket.close(); } finally { await stop(browser.child); }
        });
        const { send, evaluate } = browser;
        await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 1000, deviceScaleFactor: 1, mobile: false });
        const waitExpression = (expression, description) => waitFor(() => evaluate(expression), description);
        const navigate = async (path, interactive = false) => {
            const before = browser.connectedFrames;
            await send('Page.navigate', { url: address + path });
            await waitExpression(`location.pathname === ${JSON.stringify(path.split('?')[0])} && document.readyState === 'complete'`, 'page navigation');
            if (interactive) {
                await waitFor(() => browser.connectedFrames > before, 'interactive Blazor transport');
                await delay(500); // Allow the first server render to replace prerendered input nodes.
            }
        };
        const fill = async (selector, text) => {
            assert.equal(await evaluate(`Boolean(document.querySelector(${JSON.stringify(selector)}))`), true, 'Input not found.');
            await evaluate(`(() => {const e=document.querySelector(${JSON.stringify(selector)}); e.focus(); e.select();})()`);
            await send('Input.insertText', { text });
            await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
            await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
            assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).value === ${JSON.stringify(text)}`), true, `Input ${selector} did not retain the submitted test value.`);
        };
        const click = async selector => {
            await evaluate(`document.querySelector(${JSON.stringify(selector)})?.scrollIntoView({ block:'center', behavior:'instant' })`);
            let point;
            await waitFor(async () => {
                point = await evaluate(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null;
                    const r = e.getBoundingClientRect(), x=r.x+r.width/2, y=r.y+r.height/2, hit=document.elementFromPoint(x,y);
                    return r.width>0 && r.height>0 && !e.disabled && (hit===e || e.contains(hit)) ? {x,y} : null; })()`);
                return point;
            }, 'actionable pointer target');
            await send('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
            await send('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
            await send('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
        };
        const key = async (name, code) => {
            const text = name === 'Enter' ? '\r' : name === ' ' ? ' ' : undefined;
            await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key: name,
                code: name === ' ' ? 'Space' : name, windowsVirtualKeyCode: code, ...(text ? { text, unmodifiedText: text } : {}) });
            await send('Input.dispatchKeyEvent', { type: 'keyUp', key: name, code: name === ' ' ? 'Space' : name, windowsVirtualKeyCode: code });
        };
        await navigate('/', true);
        await fill('#username', username); await fill('#password', initialPassword);
        await evaluate("document.querySelector('form[aria-label=\"Login\"]').requestSubmit()");
        await waitExpression("location.pathname === '/password-replacement' && Boolean(document.querySelector('#current-password'))", 'required password replacement');
        await delay(500); // Password replacement starts a new interactive circuit after the form POST.
        await fill('#current-password', initialPassword); await fill('#new-password', replacementPassword); await fill('#confirm-new-password', replacementPassword);
        await evaluate("document.querySelector('form[aria-label=\"Replace password\"]').requestSubmit()");
        try { await waitExpression("location.pathname === '/home' && Boolean(document.querySelector('.theme-toggle'))", 'authenticated home'); }
        catch {
            const result = await evaluate("({ replacementFailed:new URLSearchParams(location.search).has('failed'), confirmationFailed:new URLSearchParams(location.search).has('confirmationFailed'), atReplacement:location.pathname === '/password-replacement', atHome:location.pathname === '/home' })");
            throw new Error(`Password replacement did not reach authenticated home: ${JSON.stringify(result)}.`);
        }
        await waitFor(() => browser.connectedFrames > 0, 'authenticated interactive circuit');
        for (const product of ['UI-A', 'UI-B']) {
            await navigate('/products/create', true);
            await fill('.product-identity input:not([readonly])', product);
            await fill('.product-identity .mud-input-control:nth-child(2) input', `Evidence product ${product}`);
            await click('.product-identity .mud-select');
            await waitExpression("Boolean(document.querySelector('.mud-popover-open .mud-list-item'))", 'product kind options');
            await click('.mud-popover-open .mud-list-item');
            await click('button[form="create-product-form"][type="submit"]');
            await waitExpression(`location.pathname === '/products/details' && new URLSearchParams(location.search).get('id') === ${JSON.stringify(product)}`, 'product creation');
        }
        await navigate('/products/details?id=UI-A', true);
        const productGroups = '[id^="product-navigation-children-"]';
        const developmentButtons = 'button[aria-controls^="product-navigation-development-"]';
        const state = () => evaluate(`(() => ({
            url:location.href,
            parents:[...document.querySelectorAll(${JSON.stringify(productGroups)})].map(e=>({ id:e.id, hidden:e.hidden })),
            links:[...document.querySelectorAll('#product-navigation-list a')].map(e=>({ href:e.getAttribute('href'), current:e.getAttribute('aria-current') })),
            nodes:[...document.querySelectorAll(${JSON.stringify(developmentButtons)})].map(e=>({
                id:e.getAttribute('aria-controls'), expanded:e.getAttribute('aria-expanded'), hidden:document.getElementById(e.getAttribute('aria-controls')).hidden }))
        }))()`);
        {
            await check('interaction', productChecks[0], async () => {
                assert.equal(await evaluate(`document.querySelectorAll(${JSON.stringify(productGroups)}).length`), 2);
            });
            const parentButton = 'button[aria-controls="product-navigation-children-UI-A"]';
            await check('interaction', productChecks[1], async () => {
                const before = await state();
                await click(parentButton);
                await waitFor(async () => (await state()).parents[0].hidden, 'product collapse');
                const after = await state();
                assert.equal(after.parents[1].hidden, before.parents[1].hidden);
                assert.deepEqual(after.links, before.links); assert.equal(after.url, before.url);
                assert.equal(await evaluate(`document.querySelector(${JSON.stringify(parentButton)}).getAttribute('aria-expanded')`), 'false');
            });
            await check('accessibility', productChecks[2], async () => {
                await evaluate(`document.querySelector(${JSON.stringify(parentButton)}).focus()`);
                await key('Enter', 13);
                await waitFor(async () => !(await state()).parents[0].hidden, 'keyboard product expansion');
                assert.equal(await evaluate(`document.querySelector(${JSON.stringify(parentButton)}).getAttribute('aria-expanded')`), 'true');
                await key(' ', 32);
                await waitFor(async () => (await state()).parents[0].hidden, 'keyboard product collapse');
                assert.equal(await evaluate(`document.querySelector(${JSON.stringify(parentButton)}).getAttribute('aria-expanded')`), 'false');
                await key('Enter', 13);
                await waitFor(async () => !(await state()).parents[0].hidden, 'restore product expansion');
            });
            if (development) {
                await check('interaction', developmentChecks[0], async () => {
                    const initial = await state(); assert.equal(initial.nodes.length, 2);
                    assert.ok(initial.nodes.every(node => node.expanded === 'false' && node.hidden));
                });
                const before = await state();
                const first = developmentButton('UI-A');
                await check('interaction', developmentChecks[1], async () => {
                    await click(first);
                    await waitFor(async () => (await state()).nodes[0].expanded === 'true', 'Development expansion');
                    const after = await state();
                    assert.deepEqual(after.parents, before.parents); assert.deepEqual(after.links, before.links); assert.equal(after.url, before.url);
                    assert.equal(after.nodes[0].hidden, false); assert.equal(after.nodes[1].expanded, 'false'); assert.equal(after.nodes[1].hidden, true);
                });
                await check('accessibility', developmentChecks[2], async () => {
                    await evaluate(`document.querySelector(${JSON.stringify(first)}).focus()`);
                    assert.equal(await evaluate(`document.activeElement.matches(${JSON.stringify(first)})`), true);
                    await key('Enter', 13);
                    await waitFor(async () => (await state()).nodes[0].expanded === 'false', 'Enter collapse');
                    assert.equal((await state()).nodes[0].hidden, true);
                    await key(' ', 32);
                    await waitFor(async () => (await state()).nodes[0].expanded === 'true', 'Space expansion');
                    assert.equal((await state()).nodes[0].hidden, false);
                    assert.equal((await state()).nodes[1].expanded, 'false');
                });
                await check('accessibility', developmentChecks[3], async () => {
                    const label = await evaluate(`(() => { const e=document.querySelector('${childGroup('UI-A')} .product-navigation-specifications'); return {
                        text:e?.textContent.trim(), disabled:e?.getAttribute('aria-disabled'), tabIndex:e?.tabIndex,
                        href:e?.getAttribute('href'), interactive:e?.matches('a,button,input,select,textarea,[contenteditable="true"]'),
                        childControls:e?.querySelectorAll('a,button,input,select,textarea,[tabindex]').length,
                        icon:Boolean(e?.querySelector('svg')) }; })()`);
                    assert.equal(label.text, 'Specifications'); assert.equal(label.disabled, 'true'); assert.equal(label.tabIndex, -1);
                    assert.equal(label.href, null); assert.equal(label.interactive, false); assert.equal(label.childControls, 0); assert.equal(label.icon, true);
                    const beforeLabelClick = await state();
                    await click(`${childGroup('UI-A')} .product-navigation-specifications`);
                    assert.deepEqual(await state(), beforeLabelClick);
                    assert.equal((await state()).url, before.url);
                    await evaluate(`document.querySelector(${JSON.stringify(first)}).focus()`); await key('Tab', 9);
                    assert.equal(await evaluate("document.activeElement.closest('.product-navigation-specifications') === null"), true);
                });
            }
            const inspect = id => evaluate(`(${inspectDevelopment})(${JSON.stringify(developmentButton(id))})`);
            const setDevelopment = async (id, expanded) => {
                const selector = developmentButton(id);
                const expression = `document.querySelector(${JSON.stringify(selector)}).getAttribute('aria-expanded')`;
                if (await evaluate(expression) !== String(expanded)) await click(selector);
                await waitExpression(`${expression} === ${JSON.stringify(String(expanded))}`, 'native Development state');
            };
            const requireSyntheticPage = async () => {
                assert.equal(await evaluate(`location.href === ${JSON.stringify(address + '/products/details?id=UI-A')}`), true,
                    'Only the authenticated synthetic product details page may be captured.');
            };
            // A failed AX or PNG command is a failed capture check, even if earlier pairs succeeded.
            const captureCommand = async (method, params) => {
                try { return await send(method, params); }
                catch (error) {
                    if (!(error instanceof BrowserProtocolError)) throw error;
                    throw new Error('Chromium could not collect the required PNG/AX pair.');
                }
            };
            for (const dark of [false, true]) {
                await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 1000, deviceScaleFactor: 1, mobile: false });
                if (dark) { await click('.theme-toggle'); await waitExpression("Boolean(document.querySelector('.theme-dark'))", 'dark theme'); }
                for (const width of [1280, 390]) {
                    await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
                    if (width === 390 && !await evaluate("Boolean(document.querySelector('.navigation-open'))")) {
                        await click('.navigation-toggle'); await waitExpression("Boolean(document.querySelector('.navigation-open'))", 'mobile navigation');
                    }
                    // Let drawer transitions finish before measuring and taking evidence.
                    await delay(350);
                    const name = `${dark ? 'dark' : 'light'}-${width}`;
                    await check('accessibility', layoutCheck(name), async () => {
                        const layout = await evaluate(`(() => {const r=document.querySelector('#app-navigation').getBoundingClientRect();
                            return { left:r.left, right:r.right, width:r.width, overflow:document.documentElement.scrollWidth>innerWidth };})()`);
                        assert.ok(layout.left >= -1 && layout.right <= width + 1 && layout.width > 0); assert.equal(layout.overflow, false);
                    });
                    if (!development) {
                        await requireSyntheticPage();
                        const image = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
                        const accessibility = await send('Accessibility.getFullAXTree');
                        assert.ok(accessibility.nodes.length > 0, 'Accessibility tree is empty.');
                        captures.push({ key: name, image: Buffer.from(image.data, 'base64'), tree: JSON.stringify(accessibility, null, 2) });
                        continue;
                    }
                    for (const state of states) {
                        const key = stateKey(name, state), context = captureContext(name, state);
                        const expanded = state === 'expanded';
                        await check('accessibility', captureCheck(key), async () => {
                            await setDevelopment('UI-A', expanded);
                            await evaluate(`document.querySelector(${JSON.stringify(developmentButton('UI-A'))}).scrollIntoView({ block:'center', behavior:'instant' })`);
                            await requireSyntheticPage();
                            assert.equal(await evaluate(`Boolean(document.querySelector('.app-shell.theme-${context.theme}'))`), true, 'Unexpected capture theme.');
                            const first = await inspect('UI-A');
                            assertDevelopmentDom(first, expanded);
                            assertDevelopmentDom(await inspect('UI-B'), false);
                            assert.equal(first.unobscured, true, 'Capture rows are not visible in the drawer.');
                            const clip = developmentClip(first.geometry, context);
                            const { nodes } = await captureCommand('Accessibility.getFullAXTree');
                            assertDevelopmentTree(nodes, state);
                            const screenshot = await captureCommand('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false, clip });
                            assert.equal(typeof screenshot?.data, 'string', 'Chromium did not return PNG data.');
                            const image = Buffer.from(screenshot.data, 'base64');
                            assert.ok(validPng(image, clip), 'Chromium did not return a decodable PNG of the requested crop dimensions.');
                            captures.push({ key, clip, geometry: first.geometry, image,
                                tree: JSON.stringify({ capture: { ...context, pngSha256: imageHash(image) }, nodes }, null, 2) });
                        });
                    }
                    await check('interaction', peerCheck(name), async () => {
                        const before = await state();
                        await setDevelopment('UI-B', true);
                        assertDevelopmentDom(await inspect('UI-B'), true);
                        assertDevelopmentDom(await inspect('UI-A'), true);
                        await setDevelopment('UI-B', false);
                        assertDevelopmentDom(await inspect('UI-B'), false);
                        assert.deepEqual(await state(), before);
                    });
                    await check('accessibility', styleCheck(name), async () => {
                        const colors = await evaluate(`(() => { const e=document.querySelector('${childGroup('UI-A')} .product-navigation-specifications'),
                            s=getComputedStyle(e), side=getComputedStyle(document.querySelector('#app-navigation'));
                            const canvas=document.createElement('canvas'), c=canvas.getContext('2d');
                            const rgb=color=>{ c.clearRect(0,0,1,1); c.fillStyle=color; c.fillRect(0,0,1,1); return [...c.getImageData(0,0,1,1).data]; };
                            return { foreground:rgb(s.color), background:rgb(side.backgroundColor),
                                link:rgb(getComputedStyle(document.querySelector('#product-navigation-list a')).color),
                                rect:e.getBoundingClientRect().toJSON(), cursor:s.cursor }; })()`);
                        assert.equal(colors.foreground[3], 255); assert.equal(colors.background[3], 255);
                        assert.ok(contrast(colors.foreground.slice(0,3), colors.background.slice(0,3)) >= 4.5, 'Readable disabled label contrast.');
                        assert.notDeepEqual(colors.foreground, colors.link); assert.ok(colors.rect.width > 0 && colors.rect.height > 0);
                        assert.notEqual(colors.cursor, 'pointer');
                    });
                }
            }
        }
    } catch (error) {
        if (!(error instanceof ScenarioAssertionFailure)) throw error;
    }
    return { checks, captures };
}

export async function verify({ env = process.env, repository = process.cwd(), scenario = runScenario } = {}) {
    const repo = await repositoryRoot(repository);
    const result = await withScenario({ repo, env, development: true, scenario });
    const verdict = scenarioVerdict(result);
    console.log(`${verdict.name}: ${verdict.status}; isolated resources cleaned up.`);
    return verdict.status === 'passed' ? 0 : 1;
}

export async function collect({ env = process.env, repository = process.cwd(), development = false, scenario = runScenario } = {}) {
    const context = factoryContext(env);
    const repo = await repositoryRoot(repository);
    // The factory creates this directory. Resolve it before creating any output.
    const output = await realpath(context.directory);
    assert.ok(!isInside(repo, output), 'Evidence must be outside the target worktree.');
    assert.equal((await readdir(output)).length, 0, 'Use a fresh, empty evidence directory.');
    const buildId = await command('git', ['rev-parse', 'HEAD'], { cwd: repo });
    const { checks, captures } = await withScenario({ repo, env, development, scenario });
    const verdict = scenarioVerdict({ checks, captures }, development);
    const artifacts = [], imageLinks = [], created = [];
    const write = async (path, content) => {
        const file = join(output, path);
        // Track only files we exclusively created; a failed write may still leave a partial file.
        const handle = await open(file, 'wx');
        created.push(file);
        try { await handle.writeFile(content); } finally { await handle.close(); }
    };
    try {
        if (verdict.status === 'passed') for (const key of development ? developmentCaptures : viewports) {
            const { image, tree } = captures.find(pair => pair.key === key);
            const screenshotPath = `navigation-${key}.png`, treePath = `navigation-${key}.accessibility.json`;
            await write(screenshotPath, image); await write(treePath, tree);
            artifacts.push({ kind: 'screenshot', path: screenshotPath }, { kind: 'accessibility-tree', path: treePath });
            const [theme, width, state] = key.split('-');
            imageLinks.push({ checkName: development ? aggregateName : layoutCheck(key), screenshotPath,
                caption: development ? `UI-A Development ${state}, ${theme} theme, ${width}px viewport — synthetic navigation crop.` :
                    `Authenticated product navigation, ${theme} theme, ${width}px; synthetic test products only.` });
        }
        const manifest = { version: 1, taskId: context.taskId, attempt: context.attempt, capturedAt: new Date().toISOString(),
            sourceFingerprint: context.sourceFingerprint, application: { framework: 'Blazor Server / Chromium', buildId: `${buildId}:${context.sourceFingerprint}` },
            checks: [...checks.filter(check => check.name !== verdict.name), verdict], artifacts, imageLinks,
            limitations: [...limitations, ...(!development ? ['Development-specific checks were not requested.'] : [])] };
        // The manifest is the publication point, after all checks, cleanup and artifact writes.
        const stagedManifest = `.manifest-${randomUUID()}.tmp`;
        await write(stagedManifest, JSON.stringify(manifest, null, 2) + '\n');
        await rename(join(output, stagedManifest), join(output, 'manifest.json'));
        return manifest;
    } catch (error) {
        await Promise.all(created.map(file => rm(file, { force: true })));
        throw error;
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    const verifying = args.includes('--verify'), development = args.includes('--development');
    if (new Set(args).size !== args.length || args.some(arg => arg !== '--development' && arg !== '--verify') ||
        (verifying && !development)) {
        console.error('Usage: node tests/Navigation.UiEvidence.mjs [--development [--verify]]');
        process.exitCode = 1;
    } else (verifying ? verify().then(code => { process.exitCode = code; }) : collect({ development })).catch(error => {
        // Native provider output and command arguments never reach this handler.
        console.error(`Navigation UI evidence failed: ${error.message}`); process.exitCode = 1;
    });
}
