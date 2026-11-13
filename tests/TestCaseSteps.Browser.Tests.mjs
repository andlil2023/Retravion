// No packages: run the TestCasePagesCreateIncompleteDraftEditMarkdownStepsFilterAndPreserveFailedDrafts
// integration test in Release first, then node tests/TestCaseSteps.Browser.Tests.mjs.
// Layout and native keyboard checks use actual server-rendered pages, not hand-written table fixtures.
// Interactive mutation and auto-sizing configuration are covered by the component test.
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
const manifest = JSON.parse(await readFile(new URL('../src/Retravion.Web/obj/Release/net10.0/staticwebassets.build.json', import.meta.url), 'utf8'));
const mudAsset = manifest.Assets.find(asset => asset.SourceId === 'MudBlazor' && asset.RelativePath === 'MudBlazor.min.css');
assert.ok(mudAsset, 'Build the application in Release first.');
const mudCss = await readFile(mudAsset.Identity, 'utf8');
const darkThemeMarkup = await readFile(new URL('Retravion.IntegrationTests/bin/Release/net10.0/test-case-fixtures/dark-theme.css', import.meta.url), 'utf8');
const darkThemeCss = [...darkThemeMarkup.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)].map(match => match[1]).join('\n');
assert.ok(darkThemeCss.includes('--mud-palette-surface:'), 'Render the production dark theme with the component test first.');
const fixtures = new Map();
for (const name of ['details', 'editor', 'empty-editor']) {
    const html = await readFile(new URL(`Retravion.IntegrationTests/bin/Release/net10.0/test-case-fixtures/${name}.html`, import.meta.url), 'utf8');
    // No app connection or remote resources: these are authenticated SSR snapshots from the test host.
    fixtures.set('/' + name, html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
        .replace(/<link\b[^>]*>/gi, '').replace('</head>', `<style>${mudCss}\n${appCss}\n${bundledCss}</style></head>`));
}
const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'text/html');
    response.end(fixtures.get(request.url) || 'Not found');
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const profile = await mkdtemp(join(tmpdir(), 'retravion-test-case-steps-'));
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
    const result = await command('Runtime.evaluate', { expression, returnByValue: true });
    assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
    return result.result.value;
}
try {
    browser = spawn(executable, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', 'about:blank'], { stdio: 'ignore' });
    let port;
    for (let i = 0; i < 100; i++) {
        try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; }
        catch { await delay(100); }
    }
    assert.ok(port, 'Browser debugging port unavailable.');
    const page = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })).json();
    socket = new WebSocket(page.webSocketDebuggerUrl);
    socket.addEventListener('message', event => {
        const message = JSON.parse(event.data); const item = pending.get(message.id);
        if (!item) return;
        pending.delete(message.id);
        if (message.error) item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message.result);
    });
    await once(socket, 'open');
    for (const name of ['details', 'editor', 'empty-editor']) {
        await command('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/${name}` });
        for (let i = 0; i < 100 && !await evaluate(`location.pathname === '/${name}' && Boolean(document.querySelector('.test-case-steps table'))`); i++) await delay(50);
        assert.equal(await evaluate("document.querySelectorAll('.test-case-steps tbody > tr').length"), name === 'empty-editor' ? 1 : 2);
        if (name === 'details') {
            assert.equal(await evaluate("document.querySelector('.test-case-steps strong').textContent"), 'Second');
            // Stress wrapping with long prose, an unbroken identifier and preformatted Markdown content.
            await evaluate(`(() => {
                const content = document.querySelector('.step-markdown .product-markdown-content');
                const p = document.createElement('p'); p.textContent = 'Long procedure wording '.repeat(25) + 'X'.repeat(150); content.append(p);
                const pre = document.createElement('pre'); pre.textContent = 'Code or output '.repeat(40); content.append(pre);
            })()`);
        }
        for (const dark of [false, true]) {
            await evaluate(`(() => {
                document.querySelector('#test-palette')?.remove();
                const style = document.createElement('style'); style.id = 'test-palette';
                style.textContent = ${JSON.stringify(darkThemeCss)};
                if (${dark}) document.body.append(style);
            })()`);
            const surface = await evaluate("getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-surface').trim()");
            assert.equal(surface.replace(/\s/g, '').toLowerCase(), dark ? 'rgba(25,44,59,1)' : 'rgba(255,255,255,1)', 'Actual production palette must change.');
            for (const width of [1280, 961, 960, 768, 390, 320]) {
                await command('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
                const layout = await evaluate(`(() => {
                    const table = document.querySelector('.test-case-steps table');
                    const row = table.querySelector('tbody > tr');
                    const cells = [...row.children];
                    const bounds = cells.map(cell => cell.getBoundingClientRect());
                    return {
                        overflow: document.documentElement.scrollWidth > innerWidth,
                        tableOverflow: table.scrollWidth > table.clientWidth + 1,
                        stacked: cells.length > 2 && bounds[2].top > bounds[1].top,
                        sideBySide: cells.length > 2 && Math.abs(bounds[2].top - bounds[1].top) < 1 && bounds[2].left > bounds[1].left,
                        labels: cells.map(cell => cell.dataset.label),
                        headers: [...table.querySelectorAll('thead th')].map(cell => {
                            const rect = cell.getBoundingClientRect();
                            return { text: cell.textContent.trim(), width: rect.width, height: rect.height, top: rect.top };
                        }),
                        emptyText: row.textContent.trim(),
                        emptySpansTable: Math.abs(bounds[0].width - table.getBoundingClientRect().width) < 2,
                        editableWidths: [...table.querySelectorAll('textarea')].map(field => field.getBoundingClientRect().width),
                        clipped: [...table.querySelectorAll('.step-markdown')].some(content => content.scrollHeight > content.clientHeight + 1)
                    };
                })()`);
                assert.equal(layout.overflow, false, `${name}: no page overflow at ${width}px, dark=${dark}`);
                assert.equal(layout.tableOverflow, false, `${name}: no table overflow at ${width}px`);
                assert.equal(layout.clipped, false, `${name}: no clipped content`);
                if (name === 'empty-editor') {
                    assert.equal(layout.emptyText, 'No steps yet.');
                    assert.equal(layout.emptySpansTable, true, 'Empty message spans the table.');
                    if (width > 960) {
                        assert.deepEqual(layout.headers.map(header => header.text), ['Step', 'Action', 'Expected result', 'Step actions']);
                        for (const header of layout.headers) {
                            assert.ok(header.width > 60, `Readable empty heading ${header.text}: ${header.width}px at ${width}px`);
                            assert.ok(header.height < 65, `Empty heading must not wrap vertically: ${header.text}`);
                            assert.equal(header.top, layout.headers[0].top, 'Headings align.');
                        }
                    }
                } else {
                    assert.deepEqual(layout.labels.slice(0, 3), ['Step', 'Action', 'Expected result']);
                    assert.equal(width <= 960 ? layout.stacked : layout.sideBySide, true, `${name}: responsive layout at ${width}px`);
                }
                for (const fieldWidth of layout.editableWidths) assert.ok(fieldWidth > 70, `Usable field width: ${fieldWidth}`);
                if (process.env.RETRAVION_TEST_CASE_SCREENSHOT_DIR && [1280, 390].includes(width)) {
                    const { data } = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
                    await writeFile(join(process.env.RETRAVION_TEST_CASE_SCREENSHOT_DIR, `${name}-${width}-${dark ? 'dark' : 'light'}.png`), Buffer.from(data, 'base64'));
                }
                console.log(`PASS: ${name}, ${width}px, ${dark ? 'dark' : 'light'}`);
            }
        }
        if (name === 'editor') {
            await evaluate("[...document.querySelectorAll('.test-case-steps textarea')].at(-1).focus()");
            await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
            await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
            assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), 'Insert step above step 2');
            assert.equal(await evaluate("document.querySelectorAll('.test-case-steps tbody > tr').length"), 2);
            console.log('PASS: native Tab reaches row controls without creating a step (interactive handler also verified by component test)');
        }
    }
} finally {
    socket?.close();
    if (browser && browser.exitCode === null) { browser.kill(); await Promise.race([once(browser, 'exit'), delay(3000)]); }
    server.closeAllConnections(); server.close();
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
}
