// No packages: node tests/MarkdownFullscreen.Browser.Tests.mjs
// Real Chromium checks of the production fullscreen module/scoped CSS with a minimal editor fixture.
// Set RETRAVION_TEST_BROWSER to a Chromium executable if neither Windows default below exists.
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';

const executable = process.env.RETRAVION_TEST_BROWSER || [
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe'
].find(existsSync);
assert.ok(executable, 'Set RETRAVION_TEST_BROWSER to an installed Chromium executable.');
const moduleSource = await readFile(new URL('../src/Retravion.Web/wwwroot/js/markdownFullscreen.js', import.meta.url), 'utf8');
const editorCss = (await readFile(new URL('../src/Retravion.Web/Features/Product/Description/DescriptionEditor.razor.css', import.meta.url), 'utf8')).replaceAll('::deep', '');
const fixture = `<!doctype html><html><head><meta charset="utf-8"><style>
:root { --mud-palette-background:white; --mud-palette-text-primary:black; --mud-palette-divider:gray; }
body { margin:0; } ${editorCss}
</style></head><body><button id="background">Background action</button><div style="height:100px"></div>
<dialog open class="description-editor-shell" role="group" aria-label="Markdown editor">
<form><div class="description-toolbar"><h2>Description</h2><div class="description-tools">
<button type="button" data-markdown-fullscreen-toggle>Fullscreen Markdown editor</button>
<button type="button" id="arrangement">Stacked</button></div></div>
<div class="description-layout side-by-side"><div class="description-source"><textarea rows="12">Original</textarea></div>
<div class="description-splitter" role="separator" tabindex="0" aria-orientation="vertical"
aria-label="Resize Markdown and preview panes" aria-valuemin="25" aria-valuemax="75" aria-valuenow="50"></div>
<section class="description-preview"><h3>Preview</h3><div style="height:1600px">Long preview</div></section></div></form>
<div class="product-actions"><button type="submit" id="save">Save</button><a href="#cancel" id="cancel">Cancel</a></div>
</dialog><button id="confirmation" hidden>Confirm status change</button><script type="module">
import { setFullscreen, initializeSplitter, dispose } from '/module.js';
const dialog = document.querySelector('dialog');
initializeSplitter(dialog);
window.root = dialog;
window.editor = dialog.querySelector('textarea');
window.toggle = dialog.querySelector('[data-markdown-fullscreen-toggle]');
window.changeMode = enabled => { setFullscreen(dialog, enabled); toggle.textContent = enabled ? 'Exit fullscreen' : 'Fullscreen Markdown editor'; };
toggle.onclick = () => changeMode(!dialog.matches(':modal'));
dialog.addEventListener('cancel', event => { event.preventDefault(); changeMode(false); });
window.backgroundClicks = 0;
document.querySelector('#background').onclick = () => backgroundClicks++;
document.querySelector('#arrangement').onclick = () => dialog.querySelector('.description-layout').className = 'description-layout stacked';
// Model the component's status-save sequence: exit the native modal before opening confirmation.
document.querySelector('#save').onclick = event => { event.preventDefault(); changeMode(false); const confirmation = document.querySelector('#confirmation'); confirmation.hidden = false; confirmation.focus(); };
window.cleanupEditor = () => { dialog.remove(); dispose(dialog); };
window.ready = true;
</script></body></html>`;
const server = createServer((request, response) => {
    response.setHeader('Content-Type', request.url === '/module.js' ? 'text/javascript' : 'text/html');
    response.end(request.url === '/module.js' ? moduleSource : fixture);
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const profile = await mkdtemp(join(tmpdir(), 'retravion-fullscreen-browser-'));
let browser, socket;
let nextId = 0;
const pending = new Map();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function command(method, params = {}) {
    const id = ++nextId;
    const result = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    socket.send(JSON.stringify({ id, method, params }));
    return result;
}
async function evaluate(expression) {
    const response = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    assert.ok(!response.exceptionDetails, JSON.stringify(response.exceptionDetails));
    return response.result.value;
}
async function waitFor(expression) {
    for (let attempt = 0; attempt < 200; attempt++) {
        if (await evaluate(expression)) return;
        await delay(50);
    }
    assert.fail(`Timed out: ${expression}`);
}
async function key(key, code, virtualKey) {
    for (const type of ['keyDown', 'keyUp'])
        await command('Input.dispatchKeyEvent', { type, key, code, windowsVirtualKeyCode: virtualKey, nativeVirtualKeyCode: virtualKey });
}
try {
    browser = spawn(executable, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
        '--no-first-run', '--no-default-browser-check', 'about:blank'], { stdio: 'ignore' });
    browser.on('error', error => { console.error(error.message); });
    let port;
    for (let attempt = 0; attempt < 100; attempt++) {
        try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; }
        catch { await delay(100); }
    }
    assert.ok(port, 'Browser did not expose its test debugging port.');
    const page = await (await fetch(`http://127.0.0.1:${port}/json/new?http://127.0.0.1:${server.address().port}`, { method: 'PUT' })).json();
    socket = new WebSocket(page.webSocketDebuggerUrl);
    socket.addEventListener('message', event => {
        const message = JSON.parse(event.data);
        const item = pending.get(message.id);
        if (!item) return;
        pending.delete(message.id);
        if (message.error) item.reject(new Error(JSON.stringify(message.error)));
        else item.resolve(message.result);
    });
    await once(socket, 'open');
    for (let attempt = 0; attempt < 100 && !await evaluate('Boolean(window.ready)'); attempt++) await delay(50);
    assert.equal(await evaluate('Boolean(window.ready)'), true);
    await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
    await evaluate(`window.splitter = root.querySelector('[role=separator]');
        window.layout = root.querySelector('.description-layout');
        editor.value = 'Splitter draft'; editor.setSelectionRange(2, 7, 'backward');`);
    const splitValue = () => evaluate('Number(splitter.getAttribute("aria-valuenow"))');
    const handlePosition = () => evaluate('(() => { const r = splitter.getBoundingClientRect(); return { x:r.x + r.width/2, y:r.y + 30 }; })()');
    let handle = await handlePosition();
    await command('Input.dispatchMouseEvent', { type: 'mousePressed', ...handle, button: 'left', clickCount: 1 });
    await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 900, y: handle.y, button: 'left', buttons: 1 });
    await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 900, y: handle.y, button: 'left', clickCount: 1 });
    assert.ok(await splitValue() > 65, 'Mouse drag expands Markdown.');
    assert.ok(await evaluate('root.querySelector(".description-source").clientWidth > root.querySelector(".description-preview").clientWidth'));
    await key('Home', 'Home', 36);
    assert.equal(await splitValue(), 25);
    await key('ArrowLeft', 'ArrowLeft', 37);
    assert.equal(await splitValue(), 25, 'Minimum width is bounded.');
    await key('End', 'End', 35);
    await key('ArrowRight', 'ArrowRight', 39);
    assert.equal(await splitValue(), 75, 'Maximum width is bounded.');
    await key('ArrowLeft', 'ArrowLeft', 37);
    assert.equal(await splitValue(), 73);
    await command('Emulation.setTouchEmulationEnabled', { enabled: true });
    handle = await handlePosition();
    await command('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...handle, id: 1 }] });
    await command('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: 510, y: handle.y, id: 1 }] });
    await command('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    assert.ok(await splitValue() >= 38 && await splitValue() <= 42, 'Touch drag resizes panes.');
    const chosenSplit = await splitValue();
    await command('Emulation.setTouchEmulationEnabled', { enabled: false });
    await evaluate('changeMode(true); changeMode(false)');
    assert.equal(await splitValue(), chosenSplit, 'Fullscreen retains the chosen split.');
    assert.deepEqual(await evaluate('[editor.value, editor.selectionStart, editor.selectionEnd, editor.selectionDirection]'), ['Splitter draft', 2, 7, 'backward']);
    await evaluate('layout.className = "description-layout stacked"');
    assert.equal(await evaluate('splitter.getClientRects().length'), 0);
    await evaluate('layout.className = "description-layout side-by-side"');
    assert.equal(await splitValue(), chosenSplit);
    await command('Emulation.setDeviceMetricsOverride', { width: 390, height: 640, deviceScaleFactor: 1, mobile: false });
    assert.equal(await evaluate('splitter.getClientRects().length'), 0, 'Narrow-screen splitter is hidden.');
    assert.ok(await evaluate('Math.abs(root.querySelector(".description-source").getBoundingClientRect().width - layout.clientWidth) < 1'));
    await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
    assert.equal(await splitValue(), chosenSplit);
    console.log('PASS: splitter mouse/touch/keyboard, bounds, pane geometry, draft/selection, fullscreen/layout retention and narrow-screen fallback');
    for (const [width, height] of [[1280, 800], [390, 640]]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
        await evaluate(`editor.value = 'Unsaved markdown\\n' + 'line\\n'.repeat(100); editor.setSelectionRange(3, 12, 'backward');
            editor.scrollTop = 80; window.savedScroll = editor.scrollTop;
            document.querySelector('#arrangement').click(); toggle.focus(); toggle.click();`);
        assert.equal(await evaluate("root.matches(':modal')"), true);
        assert.deepEqual(await evaluate('[editor.selectionStart, editor.selectionEnd, editor.selectionDirection, editor.scrollTop]'), [3, 12, 'backward', await evaluate('savedScroll')]);
        assert.equal(await evaluate("root.querySelector('.description-layout').className"), 'description-layout stacked');
        assert.equal(await evaluate('document.activeElement === editor'), true);
        const geometry = await evaluate('({ width: root.getBoundingClientRect().width, height: root.getBoundingClientRect().height, x: root.getBoundingClientRect().x, y: root.getBoundingClientRect().y })');
        assert.deepEqual(geometry, { width, height, x: 0, y: 0 });
        assert.equal(await evaluate("root.querySelector('.description-preview').scrollHeight > root.querySelector('.description-preview').clientHeight"), true);
        assert.equal(await evaluate("document.documentElement.style.overflow"), 'hidden');
        await evaluate("document.querySelector('#background').focus()");
        assert.equal(await evaluate('root.contains(document.activeElement)'), true, 'Background cannot take focus.');
        // A real pointer click at the background button's normal coordinates cannot activate it through the modal.
        await command('Input.dispatchMouseEvent', { type: 'mousePressed', x: 30, y: 10, button: 'left', clickCount: 1 });
        await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 30, y: 10, button: 'left', clickCount: 1 });
        assert.equal(await evaluate('backgroundClicks'), 0);
        for (let count = 0; count < 12; count++) {
            await key('Tab', 'Tab', 9);
            assert.equal(await evaluate('root.contains(document.activeElement) || document.activeElement === document.body'), true);
            assert.equal(await evaluate("document.activeElement === document.querySelector('#background')"), false);
        }
        await key('Escape', 'Escape', 27);
        assert.equal(await evaluate("root.matches(':modal')"), false);
        assert.equal(await evaluate('document.activeElement === toggle'), true);
        assert.deepEqual(await evaluate('[editor.selectionStart, editor.selectionEnd, editor.selectionDirection]'), [3, 12, 'backward']);
        assert.equal(await evaluate("editor.value.startsWith('Unsaved markdown')"), true);
        assert.equal(await evaluate("root.querySelector('.description-layout').className"), 'description-layout stacked');
        assert.equal(await evaluate("document.documentElement.style.overflow"), '');
        await evaluate("changeMode(true); toggle.click()");
        assert.equal(await evaluate("root.matches(':modal')"), false);
        assert.equal(await evaluate('document.activeElement === toggle'), true);
        console.log(`PASS: ${width}x${height} modal, draft/selection, focus/background isolation, Escape/toggle and scroll behavior`);
    }
    await evaluate("changeMode(true); document.querySelector('#save').click()");
    assert.equal(await evaluate("root.matches(':modal')"), false, 'Status save exits the native modal before confirmation.');
    assert.equal(await evaluate("document.activeElement === document.querySelector('#confirmation')"), true, 'Confirmation outside the editor can receive focus.');
    assert.ok(await evaluate("editor.value.startsWith('Unsaved markdown')"), 'Status save preserves the draft.');
    console.log('PASS: status-save modal exit permits outside confirmation focus and retains draft');
    await evaluate("editor.disabled = true; changeMode(true)");
    assert.equal(await evaluate('document.activeElement === toggle'), true, 'Busy editor still permits exiting fullscreen.');
    await evaluate('cleanupEditor()');
    assert.equal(await evaluate("document.documentElement.style.overflow"), '', 'Disposal restores page scrolling.');
    console.log('PASS: busy fallback focus and disposal cleanup');
    // Optional end-to-end pass against a separately started disposable app/database.
    // Credentials are supplied only via environment, never printed by these checks.
    if (process.env.RETRAVION_FULLSCREEN_APP_URL) {
        const app = new URL(process.env.RETRAVION_FULLSCREEN_APP_URL);
        const username = process.env.RETRAVION_FULLSCREEN_USERNAME;
        const password = process.env.RETRAVION_FULLSCREEN_PASSWORD;
        assert.ok(username && password, 'Supply disposable-app login credentials.');
        await command('Page.navigate', { url: app.href });
        await waitFor('Boolean(document.querySelector("input[name=username]"))');
        await evaluate(`(async () => {
            const token = html => new DOMParser().parseFromString(html, 'text/html').querySelector('input[name=__RequestVerificationToken]').value;
            const login = await fetch('/identity/login', { method: 'POST', body: new URLSearchParams({
                username: ${JSON.stringify(username)}, password: ${JSON.stringify(password)},
                __RequestVerificationToken: token(await (await fetch('/')).text()) }) });
            if (new URL(login.url).pathname === '/password-replacement') {
                const replacement = 'FullscreenReplacement123!';
                await fetch('/identity/password-replacement', { method: 'POST', body: new URLSearchParams({
                    currentPassword: ${JSON.stringify(password)}, newPassword: replacement, confirmNewPassword: replacement,
                    __RequestVerificationToken: token(await login.text()) }) });
            }
        })()`);
        await command('Page.navigate', { url: new URL('/products/create', app).href });
        await waitFor('Boolean(document.querySelector("[data-markdown-fullscreen-toggle]"))');
        // Wait for interactive server startup, not just prerendered markup.
        await delay(1000);
        await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
        await waitFor('Boolean(document.querySelector("[role=separator][aria-valuetext]"))');
        await evaluate('document.querySelector("[role=separator]").focus()');
        await key('ArrowRight', 'ArrowRight', 39);
        assert.equal(await evaluate('document.querySelector("[role=separator]").getAttribute("aria-valuenow")'), '52');
        await evaluate(`window.actualEditor = document.querySelector('textarea');
            actualEditor.value = '## Browser fullscreen draft'; actualEditor.setSelectionRange(3, 10, 'backward');
            actualEditor.dispatchEvent(new Event('input', { bubbles: true }));
            document.querySelector('[aria-label="Stacked"]').click();`);
        await waitFor('Boolean(document.querySelector(".description-layout.stacked"))');
        await evaluate("document.querySelector('[data-markdown-fullscreen-toggle]').click()");
        await waitFor("document.querySelector('dialog').matches(':modal')");
        assert.deepEqual(await evaluate('[actualEditor.selectionStart, actualEditor.selectionEnd, actualEditor.selectionDirection]'), [3, 10, 'backward']);
        assert.equal(await evaluate('document.querySelector("[role=separator]").getAttribute("aria-valuenow")'), '52', 'Actual Blazor renders retain the JS-managed split.');
        await key('Escape', 'Escape', 27);
        await waitFor("!document.querySelector('dialog').matches(':modal') && document.querySelector('dialog').getAttribute('role') === 'group'");
        await waitFor("document.activeElement.hasAttribute('data-markdown-fullscreen-toggle')");
        assert.equal(await evaluate('actualEditor === document.querySelector("textarea")'), true);
        await evaluate("document.querySelector('[data-markdown-fullscreen-toggle]').click()");
        await waitFor("document.querySelector('dialog').matches(':modal')");
        await evaluate(`const invalidNameLabel = [...document.querySelectorAll('label')].find(label => label.textContent.trim().replace('*','').trim() === 'Name');
            window.nameInput = document.getElementById(invalidNameLabel.htmlFor);
            nameInput.value = '   '; nameInput.dispatchEvent(new Event('change', { bubbles: true }));`);
        await delay(200);
        await evaluate("document.querySelector('dialog form').requestSubmit()");
        await waitFor("Boolean(document.querySelector('dialog [role=alert]'))");
        assert.equal(await evaluate("document.querySelector('dialog').matches(':modal')"), true, 'Create failure stays visible inside fullscreen editor.');
        const productId = await evaluate(`(() => {
            const label = [...document.querySelectorAll('label')].find(label => label.textContent.trim().startsWith('Product ID'));
            return document.getElementById(label.htmlFor).value;
        })()`);
        await evaluate(`const nameLabel = [...document.querySelectorAll('label')].find(label => label.textContent.trim().replace('*','').trim() === 'Name');
            const name = document.getElementById(nameLabel.htmlFor); name.value = 'Fullscreen browser example';
            name.dispatchEvent(new Event('change', { bubbles: true }));`);
        await delay(200);
        await evaluate("document.querySelector('dialog form').requestSubmit()");
        await waitFor("location.pathname === '/products/details'");
        assert.equal(await evaluate("document.documentElement.style.overflow"), '', 'Successful create cleans modal scroll lock.');
        const editUrl = new URL('/products/description', app); editUrl.searchParams.set('id', productId);
        await command('Page.navigate', { url: editUrl.href });
        await waitFor('Boolean(document.querySelector("textarea"))');
        await delay(1000);
        assert.equal(await evaluate('document.querySelector("textarea").value'), '## Browser fullscreen draft');
        await evaluate("document.querySelector('[data-markdown-fullscreen-toggle]').click()");
        await waitFor("document.querySelector('dialog').matches(':modal')");
        await evaluate(`const textarea = document.querySelector('textarea'); textarea.value = '## Saved fullscreen edit';
            textarea.dispatchEvent(new Event('input', { bubbles: true }));`);
        await delay(200);
        await evaluate("document.querySelector('dialog form').requestSubmit()");
        await waitFor("location.pathname === '/products/details'");
        await waitFor("document.body.textContent.includes('Saved fullscreen edit')");
        await command('Page.navigate', { url: editUrl.href });
        await waitFor('Boolean(document.querySelector("textarea"))');
        await delay(1000);
        await evaluate("document.querySelector('[data-markdown-fullscreen-toggle]').click()");
        await waitFor("document.querySelector('dialog').matches(':modal')");
        await evaluate("[...document.querySelectorAll('dialog a')].find(link => link.textContent.trim() === 'Cancel').click()");
        await waitFor("location.pathname === '/products/details'");
        assert.equal(await evaluate("document.documentElement.style.overflow"), '', 'Cancel navigation cleans modal scroll lock.');
        console.log('PASS: actual Blazor Product create/edit, Escape, same textarea, failed-create feedback, save/cancel navigation and cleanup');
    }
    await command('Browser.close');
} finally {
    socket?.close();
    if (browser && browser.exitCode === null) {
        await Promise.race([once(browser, 'exit'), delay(3000)]);
        if (browser.exitCode === null) browser.kill();
    }
    server.close();
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
}
