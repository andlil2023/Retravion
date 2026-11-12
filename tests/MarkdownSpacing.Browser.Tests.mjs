// No packages: node tests/MarkdownSpacing.Browser.Tests.mjs (after a Release build).
// Real Chromium, production scoped CSS and actual MudBlazor stylesheet; structural fixtures.
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
const scope = bundledCss.match(/\.product-markdown-content\[([^\]]+)\]/)?.[1];
const featureScope = bundledCss.match(/\.feature-details\[([^\]]+)\]/)?.[1];
assert.ok(scope && featureScope, 'Release build must contain shared and Feature scoped styles.');
const manifest = JSON.parse(await readFile(new URL('../src/Retravion.Web/obj/Release/net10.0/staticwebassets.build.json', import.meta.url), 'utf8'));
const mudAsset = manifest.Assets.find(asset => asset.SourceId === 'MudBlazor' && asset.RelativePath === 'MudBlazor.min.css');
assert.ok(mudAsset, 'MudBlazor stylesheet must be available after building.');
const mudCss = await readFile(mudAsset.Identity, 'utf8');
const content = '<h1>Document</h1><p>First paragraph.</p><p>Second paragraph.</p>' +
    [2, 3, 4, 5, 6].map(level => '<h' + level + '>Chapter ' + level + '</h' + level + '><p>Chapter body.</p>').join('') +
    '<ul><li>First bullet<ul><li>Nested bullet</li></ul></li><li>Second bullet</li></ul><ol><li>Ordered item</li></ol>' +
    '<blockquote><p>Quoted paragraph.</p></blockquote><pre><code>Code block</code></pre><hr><p>Final paragraph.</p>';
const markdown = '<div class="product-markdown-content" ' + scope + '>' + content + '</div>';
const fixture = '<!doctype html><html><head><meta charset="utf-8"><style>' +
    ':root { --mud-palette-divider:gray; --mud-palette-surface:white; --mud-palette-text-primary:black; }' +
    mudCss + appCss + bundledCss +
    '</style></head><body><main class="page-content">' +
    '<p id="outside-before">Outside Markdown</p>' +
    '<article id="product" class="product-markdown product-card mud-paper pa-4">' + markdown + '</article>' +
    '<section id="plain">' + markdown + '</section>' +
    '<section id="feature" class="feature-details" ' + featureScope + '>' + markdown + '</section>' +
    '<section id="preview" class="description-preview">' + markdown + '</section>' +
    '<p id="outside-after">Outside Markdown</p></main></body></html>';
const server = createServer((request, response) => { response.setHeader('Content-Type', 'text/html'); response.end(fixture); });
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const profile = await mkdtemp(join(tmpdir(), 'retravion-markdown-spacing-'));
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
    for (let i = 0; i < 100 && !await evaluate('Boolean(document.querySelector("#plain"))'); i++) await delay(50);
    for (const width of [1280, 390]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height:1000, deviceScaleFactor:1, mobile:false });
        const measurements = await evaluate(`(() => {
            const gap = (a, b) => b.getBoundingClientRect().top - a.getBoundingClientRect().bottom;
            return ['product', 'plain', 'feature', 'preview'].map(id => {
                const root = document.querySelector('#' + id + ' .product-markdown-content');
                const headings = [...root.querySelectorAll('h1,h2,h3,h4,h5,h6')];
                const children = [...root.children];
                return {
                    id,
                    firstMargin: parseFloat(getComputedStyle(root.firstElementChild).marginBlockStart),
                    lastMargin: parseFloat(getComputedStyle(root.lastElementChild).marginBlockEnd),
                    chapterGaps: headings.slice(1).map(h => gap(h.previousElementSibling, h)),
                    bodyGaps: headings.map(h => gap(h, h.nextElementSibling)),
                    paragraphGap: gap(root.querySelector('p'), root.querySelector('p + p')),
                    blockGaps: children.slice(1).map((child, i) => gap(children[i], child)),
                    lists: [...root.querySelectorAll('ul,ol')].map(list => ({
                        inset: list.querySelector('li').getBoundingClientRect().left - list.getBoundingClientRect().left,
                        type: getComputedStyle(list).listStyleType
                    }))
                };
            });
        })()`);
        for (const m of measurements) {
            assert.equal(m.firstMargin, 0, m.id + ': no leading blank space');
            assert.equal(m.lastMargin, 0, m.id + ': no trailing blank space');
            for (const gap of m.chapterGaps) assert.ok(gap >= 24, m.id + ': chapter gap ' + gap);
            for (const gap of m.bodyGaps) assert.ok(gap >= 12, m.id + ': heading/body gap ' + gap);
            assert.ok(m.paragraphGap >= 12, m.id + ': paragraph spacing');
            for (const gap of m.blockGaps) assert.ok(gap >= 12, m.id + ': block gap ' + gap);
            assert.equal(m.lists.length, 3);
            for (const list of m.lists) { assert.ok(list.inset >= 24); assert.notEqual(list.type, 'none'); }
        }
        assert.equal(await evaluate('document.documentElement.scrollWidth > innerWidth'), false);
        assert.equal(await evaluate('getComputedStyle(document.querySelector("#outside-before")).marginBlockEnd'), '0px', 'Shared rules do not style outside content');
        if (process.env.RETRAVION_MARKDOWN_SPACING_SCREENSHOT_DIR) {
            const { data } = await command('Page.captureScreenshot', { format:'png', captureBeyondViewport:true });
            await writeFile(join(process.env.RETRAVION_MARKDOWN_SPACING_SCREENSHOT_DIR, 'markdown-spacing-' + width + '.png'), Buffer.from(data, 'base64'));
        }
        console.log('PASS: shared Markdown spacing and indentation in Product/plain/Feature/preview contexts at ' + width + 'px');
    }
} finally {
    socket?.close();
    if (browser && browser.exitCode === null) { browser.kill(); await Promise.race([once(browser, 'exit'), delay(3000)]); }
    server.closeAllConnections();
    server.close();
    await rm(profile, { recursive:true, force:true, maxRetries:10, retryDelay:300 });
}
