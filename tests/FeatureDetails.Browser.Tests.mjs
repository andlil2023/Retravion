// No packages: node tests/FeatureDetails.Browser.Tests.mjs (after a Release build).
// Real Chromium layout checks using production bundled CSS and the Feature details structure.
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';

const executable = process.env.RETRAVION_TEST_BROWSER || [
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe'
].find(existsSync);
assert.ok(executable, 'Set RETRAVION_TEST_BROWSER to Chromium.');
const appCss = await readFile(new URL('../src/Retravion.Web/wwwroot/css/app.css', import.meta.url), 'utf8');
const bundledCss = await readFile(new URL('../src/Retravion.Web/obj/Release/net10.0/scopedcss/bundle/Retravion.Web.styles.css', import.meta.url), 'utf8');
const scope = bundledCss.match(/\.feature-details\[([^\]]+)\]/)?.[1];
assert.ok(scope, 'Release build must contain Feature details scoped styles.');
const markdownScope = bundledCss.match(/\.product-markdown-content\[([^\]]+)\]/)?.[1];
assert.ok(markdownScope, 'Release build must contain shared Markdown scoped styles.');
const content = `<div class="product-markdown-content" ${markdownScope}><h2>Context</h2><p>First paragraph.</p><p>Second paragraph.</p><ul><li>First bullet<ul><li>Nested bullet</li></ul></li><li>Second bullet</li></ul><ol><li>Ordered item</li></ol></div>`;
// Apply the generated scope only to page-owned nodes, just as Razor does.
const fixture = `<!doctype html><html><head><meta charset="utf-8"><style>
* { box-sizing:border-box; margin:0; padding:0; }
body { font:16px/1.5 sans-serif; }
:root { --mud-palette-divider:#ccc; --mud-palette-surface:white; }
.pa-4 { padding:16px; } .mt-4 { margin-top:16px; }
${appCss}\n${bundledCss}
</style></head><body><main class="page-content"><div class="feature-details" ${scope}>
<p ${scope}>P – Product</p><h2 ${scope}>F-002 – Requirement management</h2><p ${scope}>v2 · Draft</p>
<p ${scope}>Manage links · Links indicate addressed Requirements, not implementation or verification.</p>
<nav ${scope} aria-label="Feature versions"><a>v1 · Approved</a><a>v2 · Draft</a></nav>
<div class="product-card mud-paper pa-4"><section ${scope} aria-label="Description" class="product-markdown"><h3 ${scope}>Description</h3>${content}</section></div>
<div class="product-actions" ${scope}><button>Edit Draft</button></div>
<p ${scope}>Reopening requires the latest Approved version. Approval is not release.</p>
<div class="product-card mud-paper pa-4 mt-4"><h3 ${scope}>History</h3>
<details ${scope} class="product-markdown" open><summary ${scope}>Successor · v2 · 2026-01-01 · reviewer</summary><p ${scope}>F-002 · Draft</p><h4 ${scope}>Title at event</h4><p ${scope}>Requirement management</p><h4 ${scope}>Description at event</h4>${content}</details>
<details ${scope} class="product-markdown"><summary ${scope}>Approved · v1 · 2026-01-01 · reviewer</summary></details></div>
</div></main></body></html>`;
const server = createServer((request, response) => { response.setHeader('Content-Type', 'text/html'); response.end(fixture); });
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const profile = await mkdtemp(join(tmpdir(), 'retravion-feature-details-'));
let browser, socket, nextId = 0;
const pending = new Map();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function command(method, params = {}) {
    const id = ++nextId;
    const result = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    socket.send(JSON.stringify({ id, method, params }));
    return result;
}
async function evaluate(expression) {
    const result = await command('Runtime.evaluate', { expression, returnByValue:true });
    assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
    return result.result.value;
}
try {
    browser = spawn(executable, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', 'about:blank'], { stdio:'ignore' });
    let port;
    for (let i = 0; i < 100; i++) {
        try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; }
        catch { await delay(100); }
    }
    assert.ok(port, 'Browser debugging port unavailable.');
    const page = await (await fetch(`http://127.0.0.1:${port}/json/new?http://127.0.0.1:${server.address().port}`, { method:'PUT' })).json();
    socket = new WebSocket(page.webSocketDebuggerUrl);
    socket.addEventListener('message', event => {
        const message = JSON.parse(event.data); const item = pending.get(message.id);
        if (!item) return;
        pending.delete(message.id);
        if (message.error) item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message.result);
    });
    await once(socket, 'open');
    for (let i = 0; i < 100 && !await evaluate('Boolean(document.querySelector("details"))'); i++) await delay(50);
    for (const width of [1280, 390]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height:1000, deviceScaleFactor:1, mobile:false });
        const layout = await evaluate(`(() => {
            const gap = (a, b) => b.getBoundingClientRect().top - a.getBoundingClientRect().bottom;
            const section = document.querySelector('section'); const history = document.querySelector('details');
            const markdown = section.querySelector('.product-markdown-content');
            return {
                overflow: document.documentElement.scrollWidth > innerWidth,
                sectionGap: gap(document.querySelector('nav'), section.parentElement),
                headingGap: gap(section.querySelector('h3'), markdown),
                paragraphGap: gap(...markdown.querySelectorAll('p')),
                historyGap: gap(history.parentElement.querySelector('h3'), history),
                lists: [...document.querySelectorAll('ul, ol')].map(list => ({
                    inset: list.querySelector('li').getBoundingClientRect().left - list.getBoundingClientRect().left,
                    type: getComputedStyle(list).listStyleType }))
            };
        })()`);
        assert.equal(layout.overflow, false, `No horizontal overflow at ${width}px.`);
        for (const key of ['sectionGap', 'headingGap', 'paragraphGap', 'historyGap'])
            assert.ok(layout[key] >= 12, `${key} is readable at ${width}px: ${layout[key]}`);
        assert.equal(layout.lists.length, 6);
        for (const list of layout.lists) { assert.ok(list.inset >= 24); assert.notEqual(list.type, 'none'); }
        if (process.env.RETRAVION_FEATURE_SCREENSHOT_DIR) {
            const { data } = await command('Page.captureScreenshot', { format:'png', captureBeyondViewport:true });
            await writeFile(join(process.env.RETRAVION_FEATURE_SCREENSHOT_DIR, `feature-details-${width}.png`), Buffer.from(data, 'base64'));
        }
        console.log(`PASS: Feature details description/history indentation and spacing at ${width}px`);
    }
} finally {
    socket?.close();
    if (browser && browser.exitCode === null) { browser.kill(); await Promise.race([once(browser, 'exit'), delay(3000)]); }
    server.closeAllConnections();
    server.close();
    await rm(profile, { recursive:true, force:true, maxRetries:10, retryDelay:300 });
}
