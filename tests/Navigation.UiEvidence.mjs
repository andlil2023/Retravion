// Trusted, opt-in pi-factory collector. Run from the TARGET repository root.
// Node 24+, .NET SDK from global.json, Podman and Edge/Chrome; no npm packages.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { spawn, execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, mkdir, readFile, readdir, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const delay = ms => new Promise(done => setTimeout(done, ms));
const limitations = [
    'Scoped to product navigation, not other changed pages or comprehensive accessibility compliance.',
    'Accessibility-tree and native keyboard checks do not replace assistive-technology user testing.',
    'Forced termination of the collector can prevent cleanup; inspect owned resources after interruption.'
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

async function openBrowser(executable, profile) {
    const child = spawn(executable, ['--headless=new', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0',
        `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check', 'about:blank'], { stdio: 'ignore', windowsHide: true });
    let startupError = false;
    child.on('error', () => { startupError = true; });
    let socket;
    try {
        let port;
        await waitFor(async () => {
            assert.ok(!startupError && child.exitCode === null, 'Browser did not start.');
            try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; return /^\d+$/.test(port); }
            catch { return false; }
        }, 'browser debugging port');
        const response = await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT', signal: AbortSignal.timeout(5000) });
        assert.ok(response.ok, 'Browser target unavailable.');
        const target = await response.json();
        socket = new WebSocket(target.webSocketDebuggerUrl);
        let nextId = 0, connectedFrames = 0;
        const pending = new Map(), transports = new Set();
        socket.addEventListener('message', ({ data }) => {
            const message = JSON.parse(data);
            if (message.method === 'Network.webSocketCreated' && new URL(message.params.url).pathname.endsWith('/_blazor')) transports.add(message.params.requestId);
            if (message.method === 'Network.webSocketFrameReceived' && transports.has(message.params.requestId)) connectedFrames++;
            const item = pending.get(message.id);
            if (!item) return;
            pending.delete(message.id); clearTimeout(item.timer);
            if (message.error) item.reject(new Error('Browser protocol command failed.')); else item.resolve(message.result);
        });
        socket.addEventListener('close', () => {
            for (const item of pending.values()) { clearTimeout(item.timer); item.reject(new Error('Browser connection closed.')); }
            pending.clear();
        });
        await new Promise((done, reject) => {
            const timer = setTimeout(() => reject(new Error('Browser connection timed out.')), 10_000);
            socket.addEventListener('open', () => { clearTimeout(timer); done(); }, { once: true });
            socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('Browser connection failed.')); }, { once: true });
        });
        const send = (method, params = {}) => new Promise((done, reject) => {
            const id = ++nextId;
            const timer = setTimeout(() => { pending.delete(id); reject(new Error('Browser protocol command timed out.')); }, 15_000);
            pending.set(id, { resolve: done, reject, timer });
            socket.send(JSON.stringify({ id, method, params }));
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

export async function collect({ env = process.env, repository = process.cwd(), development = false } = {}) {
    const context = factoryContext(env);
    const repo = await realpath(repository);
    assert.ok(existsSync(join(repo, 'Retravion.slnx')), 'Run from a Retravion repository root.');
    await mkdir(context.directory, { recursive: true });
    const output = await realpath(context.directory);
    assert.ok(!isInside(repo, output), 'Evidence must be outside the target worktree.');
    assert.equal((await readdir(output)).length, 0, 'Use a fresh, empty evidence directory.');
    const executable = env.RETRAVION_TEST_BROWSER || [
        'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
        'C:/Program Files/Google/Chrome/Application/chrome.exe'
    ].find(existsSync);
    assert.ok(executable && existsSync(executable), 'Set RETRAVION_TEST_BROWSER to an installed Chromium executable.');
    const buildId = await command('git', ['rev-parse', 'HEAD'], { cwd: repo });
    const id = randomUUID().replaceAll('-', '');
    const container = `retravion-ui-evidence-${id}`;
    const username = `ui-${id.slice(0, 12)}`;
    const password = () => `Aa1!${randomUUID().replaceAll('-', '').slice(0, 20)}`;
    const databasePassword = password(), initialPassword = password(), replacementPassword = password();
    const profile = await mkdtemp(join(tmpdir(), 'retravion-ui-browser-'));
    let app, browser, cleanupPromise;
    const cleanup = () => cleanupPromise ??= (async () => {
        const failures = [];
        if (browser) {
            try { await browser.send('Browser.close'); } catch { /* The protocol may close before its reply. */ }
            browser.socket.close();
            try { await stop(browser.child); } catch { failures.push('browser'); }
        }
        try { await stop(app); } catch { failures.push('application'); }
        // The random name is ours even if podman lost the create acknowledgement.
        try { await command('podman', ['rm', '--force', '--ignore', container]); } catch { failures.push('database'); }
        try { await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }); } catch { failures.push('profile'); }
        if (failures.length) throw new Error(`Cleanup failed (${failures.join(', ')}); inspect owned container ${container}.`);
    })();
    const onSignal = () => { cleanup().then(() => process.exit(1), () => process.exit(1)); };
    process.once('SIGINT', onSignal); process.once('SIGTERM', onSignal);
    const checks = [], artifacts = [], imageLinks = [];
    const check = async (kind, name, action) => {
        try { await action(); checks.push({ kind, name, status: 'passed' }); }
        catch (error) {
            // Assertions below contain only fixed expectations and synthetic navigation data, never credentials.
            checks.push({ kind, name, status: 'failed', details: error.message.slice(0, 1000) });
            console.log(`Navigation assertion failed: ${name}: ${error.message.slice(0, 1000)}`);
            throw new Error(`Check failed: ${name}.`);
        }
    };
    let operationalFailure, scenarioFailure;
    try {
        console.log(`Starting isolated navigation evidence environment (${container}).`);
        await command('dotnet', ['build', 'src/Retravion.Web/Retravion.Web.csproj', '--configuration', 'Release'], { cwd: repo, timeout: 180_000 });
        await command('podman', ['run', '--detach', '--rm', '--name', container, '--env', 'POSTGRES_PASSWORD', '--publish', '127.0.0.1::5432', 'postgres:17'],
            { env: { ...env, POSTGRES_PASSWORD: databasePassword } });
        await waitFor(async () => {
            try { await command('podman', ['exec', container, 'pg_isready', '-U', 'postgres'], { timeout: 5000 }); return true; }
            catch { return false; }
        }, 'disposable PostgreSQL readiness', 60_000);
        const binding = await command('podman', ['port', container, '5432/tcp']);
        assert.match(binding, /^127\.0\.0\.1:\d+$/, 'Unexpected PostgreSQL port binding.');
        const connection = `Host=127.0.0.1;Port=${binding.split(':')[1]};Database=postgres;Username=postgres;Password=${databasePassword}`;
        const appEnv = { ...env, DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production',
            ConnectionStrings__DefaultConnection: connection, InitialAdmin__Username: username, InitialAdmin__Password: initialPassword,
            Logging__LogLevel__Default: 'Warning', 'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' };
        // Launch the built app directly, not dotnet run, so the owned process is the server.
        app = spawn('dotnet', [join(repo, 'src/Retravion.Web/bin/Release/net10.0/Retravion.Web.dll'),
            '--contentRoot', join(repo, 'src/Retravion.Web'), '--urls', 'http://127.0.0.1:0'],
        { cwd: repo, env: appEnv, stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true });
        let address, startupError = false, startupOutput = '';
        app.on('error', () => { startupError = true; });
        app.stdout.on('data', bytes => {
            startupOutput = (startupOutput + bytes.toString()).slice(-8192);
            address = startupOutput.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1] ?? address;
        });
        await waitFor(() => {
            assert.ok(!startupError && app.exitCode === null, 'Application did not start.');
            return address;
        }, 'isolated application readiness', 60_000);
        startupOutput = '';
        browser = await openBrowser(executable, profile);
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
            try { await waitExpression(`location.pathname === '/products/details' && new URLSearchParams(location.search).get('id') === ${JSON.stringify(product)}`, 'product creation'); }
            catch {
                const image = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
                await writeFile(join(output, 'product-setup-failure.png'), Buffer.from(image.data, 'base64'));
                throw new Error('Product creation did not reach its details page; inspect product-setup-failure.png.');
            }
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
        try {
            await check('interaction', 'Two isolated products appear in product navigation', async () => {
                assert.equal(await evaluate(`document.querySelectorAll(${JSON.stringify(productGroups)}).length`), 2);
            });
            const parentButton = 'button[aria-controls="product-navigation-children-UI-A"]';
            await check('interaction', 'Product mouse toggle is independent and preserves links and location', async () => {
                const before = await state();
                await click(parentButton);
                await waitFor(async () => (await state()).parents[0].hidden, 'product collapse');
                const after = await state();
                assert.equal(after.parents[1].hidden, before.parents[1].hidden);
                assert.deepEqual(after.links, before.links); assert.equal(after.url, before.url);
                assert.equal(await evaluate(`document.querySelector(${JSON.stringify(parentButton)}).getAttribute('aria-expanded')`), 'false');
            });
            await check('accessibility', 'Product keyboard toggle updates native expanded state and visibility', async () => {
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
                await check('interaction', 'Development starts collapsed for both products', async () => {
                    const initial = await state(); assert.equal(initial.nodes.length, 2);
                    assert.ok(initial.nodes.every(node => node.expanded === 'false' && node.hidden));
                });
                const before = await state();
                const first = `${developmentButtons}:first-of-type`;
                // Each Development button is the only direct button in its product child group.
                await check('interaction', 'Mouse toggles only the targeted Development node and preserves navigation', async () => {
                    await click(first);
                    await waitFor(async () => (await state()).nodes[0].expanded === 'true', 'Development expansion');
                    const after = await state();
                    assert.deepEqual(after.parents, before.parents); assert.deepEqual(after.links, before.links); assert.equal(after.url, before.url);
                    assert.equal(after.nodes[0].hidden, false); assert.equal(after.nodes[1].expanded, 'false'); assert.equal(after.nodes[1].hidden, true);
                });
                await check('accessibility', 'Enter and Space toggle the native button with matching accessible state', async () => {
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
                await check('accessibility', 'Specifications is disabled, not a link or focus stop, and does not navigate', async () => {
                    const label = await evaluate(`(() => { const e=document.querySelector('.product-navigation-specifications'); return {
                        text:e?.textContent.trim(), disabled:e?.getAttribute('aria-disabled'), tabIndex:e?.tabIndex,
                        href:e?.getAttribute('href'), interactive:e?.matches('a,button,input,select,textarea,[contenteditable="true"]'),
                        childControls:e?.querySelectorAll('a,button,input,select,textarea,[tabindex]').length,
                        icon:Boolean(e?.querySelector('svg')) }; })()`);
                    assert.equal(label.text, 'Specifications'); assert.equal(label.disabled, 'true'); assert.equal(label.tabIndex, -1);
                    assert.equal(label.href, null); assert.equal(label.interactive, false); assert.equal(label.childControls, 0); assert.equal(label.icon, true);
                    const beforeLabelClick = await state();
                    await click('.product-navigation-specifications');
                    assert.deepEqual(await state(), beforeLabelClick);
                    assert.equal((await state()).url, before.url);
                    await evaluate(`document.querySelector(${JSON.stringify(first)}).focus()`); await key('Tab', 9);
                    assert.equal(await evaluate("document.activeElement.closest('.product-navigation-specifications') === null"), true);
                });
            }
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
                    await check('accessibility', `Navigation is visible without horizontal overflow (${name})`, async () => {
                        const layout = await evaluate(`(() => {const r=document.querySelector('#app-navigation').getBoundingClientRect();
                            return { left:r.left, right:r.right, width:r.width, overflow:document.documentElement.scrollWidth>innerWidth };})()`);
                        assert.ok(layout.left >= -1 && layout.right <= width + 1 && layout.width > 0); assert.equal(layout.overflow, false);
                    });
                    if (development) await check('accessibility', `Specifications has readable theme-derived disabled styling (${name})`, async () => {
                        const colors = await evaluate(`(() => { const e=document.querySelector('.product-navigation-specifications'),
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
                    const screenshot = `navigation-${name}.png`, tree = `navigation-${name}.accessibility.json`;
                    const image = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
                    await writeFile(join(output, screenshot), Buffer.from(image.data, 'base64'));
                    const accessibility = await send('Accessibility.getFullAXTree');
                    assert.ok(accessibility.nodes.length > 0, 'Accessibility tree is empty.');
                    if (development) await check('accessibility', `Accessibility tree exposes expanded Development and disabled Specifications (${name})`, async () => {
                        assert.ok(accessibility.nodes.some(node => node.role?.value === 'button' && node.name?.value.startsWith('Development for UI-A') &&
                            node.properties?.some(property => property.name === 'expanded' && property.value.value === true)));
                        assert.ok(hasDisabledText(accessibility.nodes, 'Specifications'), 'Specifications text must belong to an exposed disabled node.');
                    });
                    await writeFile(join(output, tree), JSON.stringify(accessibility, null, 2));
                    artifacts.push({ kind: 'screenshot', path: screenshot }, { kind: 'accessibility-tree', path: tree });
                    imageLinks.push({ checkName: development ? `Specifications has readable theme-derived disabled styling (${name})` : `Navigation is visible without horizontal overflow (${name})`,
                        screenshotPath: screenshot, caption: `Authenticated product navigation, ${dark ? 'dark' : 'light'} theme, ${width}px; synthetic test products only.` });
                }
            }
        } catch (error) {
            if (!checks.some(item => item.status === 'failed')) throw error;
            scenarioFailure = error;
            // Preserve the actual failed page too; do not turn an assertion into a missing-artifact error.
            const image = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
            await writeFile(join(output, 'navigation-failure.png'), Buffer.from(image.data, 'base64'));
            await writeFile(join(output, 'navigation-failure.accessibility.json'), JSON.stringify(await send('Accessibility.getFullAXTree'), null, 2));
            artifacts.push({ kind: 'screenshot', path: 'navigation-failure.png' }, { kind: 'accessibility-tree', path: 'navigation-failure.accessibility.json' });
        }
    } catch (error) { operationalFailure = error; }
    finally {
        try { await cleanup(); } catch (error) { operationalFailure ??= error; }
        process.removeListener('SIGINT', onSignal); process.removeListener('SIGTERM', onSignal);
    }
    if (operationalFailure) throw operationalFailure;
    // Failed assertions are a valid failed-evidence manifest, not a collector crash.
    const manifest = { version: 1, taskId: context.taskId, attempt: context.attempt, capturedAt: new Date().toISOString(),
        sourceFingerprint: context.sourceFingerprint, application: { framework: 'Blazor Server / Chromium', buildId: `${buildId}:${context.sourceFingerprint}` },
        checks, artifacts, imageLinks, limitations: [...limitations, ...(!development ? ['Development-specific checks were not requested.'] : [])] };
    await writeFile(join(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
    console.log(`Navigation evidence ${scenarioFailure ? 'contains failed checks' : 'passed'}; isolated resources cleaned up.`);
    return manifest;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    if (args.some(arg => arg !== '--development')) { console.error('Usage: node tests/Navigation.UiEvidence.mjs [--development]'); process.exitCode = 1; }
    else collect({ development: args.includes('--development') }).catch(error => {
        // Known errors are deliberately free of provider output and submitted values.
        console.error(`Navigation UI evidence failed: ${error.message}`); process.exitCode = 1;
    });
}
