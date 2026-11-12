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
const editorCss = (await readFile(new URL('../src/Retravion.Web/Components/Markdown/DescriptionEditor.razor.css', import.meta.url), 'utf8')).replaceAll('::deep', '');
const appCss = await readFile(new URL('../src/Retravion.Web/wwwroot/css/app.css', import.meta.url), 'utf8');
const fixture = `<!doctype html><html><head><meta charset="utf-8"><style>
:root { --mud-palette-background:white; --mud-palette-text-primary:black; --mud-palette-divider:gray; }
body { margin:0; } ${editorCss}
</style></head><body><button id="background">Background action</button><div style="height:100px"></div>
<dialog open class="description-editor-shell" role="group" aria-label="Markdown editor">
<div class="product-card mud-paper"><form><div class="description-editor-content"><div class="description-toolbar"><h2>Description</h2><div class="description-tools">
<button type="button" data-markdown-fullscreen-toggle>Fullscreen Markdown editor</button>
<button type="button" id="arrangement">Stacked</button></div></div>
<div class="description-layout side-by-side"><div class="description-source"><label>Description (Markdown)</label><textarea rows="12">Original</textarea></div>
<div class="description-splitter" role="separator" tabindex="0" aria-orientation="vertical"
aria-label="Resize Markdown and preview panes" aria-valuemin="25" aria-valuemax="75" aria-valuenow="50"></div>
<section class="description-preview"><h3>Preview</h3><div style="height:1600px">Long preview</div></section></div></div></form></div>
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
    const diagnostics = await evaluate('({path: location.pathname, panes: [...document.querySelectorAll(".description-layout")].map(layout => ({style: layout.getAttribute("style"), source: layout.querySelector(".description-source").getBoundingClientRect().height, preview: layout.querySelector(".description-preview").getBoundingClientRect().height})), alerts: [...document.querySelectorAll("[role=alert]")].map(alert => alert.textContent.trim()), invalidFields: [...document.querySelectorAll(":invalid")].map(field => ({id: field.id, message: field.validationMessage}))})');
    assert.fail(`Timed out: ${expression}; ${JSON.stringify(diagnostics)}`);
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
    // Resize the real textarea in both directions; preview content cannot set pane height.
    const matchingHeights = 'Math.abs(root.querySelector(".description-source").getBoundingClientRect().height - root.querySelector(".description-preview").getBoundingClientRect().height) < 1';
    for (const arrangement of ['side-by-side', 'stacked']) {
        await evaluate(`layout.className = 'description-layout ${arrangement}'`);
        for (const height of [610, 350]) {
            await evaluate(`editor.style.height = '${height}px'`);
            await waitFor(matchingHeights);
            assert.ok(await evaluate(`Math.abs(editor.getBoundingClientRect().height - ${height}) < 20`));
        }
    }
    await evaluate('layout.className = "description-layout side-by-side"');
    console.log('PASS: expanded/shrunk editor and preview heights match in both orientations');
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
        await waitFor(matchingHeights);
        const panes = await evaluate('({layout: layout.getBoundingClientRect().height, source: root.querySelector(".description-source").getBoundingClientRect().height, textarea: editor.getBoundingClientRect().height, bottom: layout.getBoundingClientRect().bottom, actions: root.querySelector(".product-actions").getBoundingClientRect().top})');
        assert.ok(panes.layout > height * 0.65, 'Fullscreen expands the editing surface, not just its background.');
        assert.ok(Math.abs(panes.bottom - panes.actions + 16) < 2, 'Panes fill the space above actions.');
        assert.ok(panes.textarea > 80, 'Textarea fits within its fullscreen pane.');
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
    // Exercise the production Product details CSS with the page's rendered structure.
    await evaluate(`(() => {
        const style = document.createElement('style'); style.textContent = ${JSON.stringify(appCss)}; document.head.append(style);
        const section = document.createElement('section'); section.id = 'details-fixture'; section.className = 'page-content';
        section.innerHTML = '<div class="product-heading"><h2>SP-001 – Retravion</h2><span class="product-status">Active</span><dl class="product-kind"><dt>Kind</dt><dd>Software application</dd></dl></div><div class="product-card mud-paper product-document"><article class="product-markdown"><p>Description</p><ul><li>Applications<ul><li>Nested</li></ul></li><li>Revisions</li></ul><ol><li>First</li><li>Second</li></ol></article></div>';
        document.body.append(section);
    })()`);
    for (const [width, theme] of [[1280, 'light'], [390, 'light'], [1280, 'dark'], [390, 'dark']]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height: 800, deviceScaleFactor: 1, mobile: false });
        const layout = await evaluate(`(() => {
            const fixture = document.querySelector('#details-fixture');
            fixture.style.setProperty('--mud-palette-text-secondary', '${theme === 'dark' ? 'rgb(200, 210, 220)' : 'rgb(80, 90, 100)'}');
            const kind = fixture.querySelector('.product-kind'); const heading = fixture.querySelector('h2');
            const article = fixture.querySelector('article'); const lists = [...article.querySelectorAll('ul, ol')];
            return { below: kind.getBoundingClientRect().top >= heading.getBoundingClientRect().bottom,
                kindColor: getComputedStyle(kind.querySelector('dt')).color,
                valueSize: parseFloat(getComputedStyle(kind.querySelector('dd')).fontSize),
                labelAboveValue: kind.querySelector('dt').getBoundingClientRect().bottom <= kind.querySelector('dd').getBoundingClientRect().top,
                clipped: article.scrollWidth > article.clientWidth,
                lists: lists.map(list => ({ padding: parseFloat(getComputedStyle(list).paddingInlineStart),
                    type: getComputedStyle(list).listStyleType,
                    itemInset: list.querySelector('li').getBoundingClientRect().left - article.getBoundingClientRect().left })) };
        })()`);
        assert.equal(layout.below, true, `Kind is below the title at ${width}px in ${theme}.`);
        assert.equal(layout.kindColor, theme === 'dark' ? 'rgb(200, 210, 220)' : 'rgb(80, 90, 100)');
        assert.ok(layout.valueSize >= 16, 'Kind value uses normal readable text size.');
        assert.equal(layout.labelAboveValue, true, 'Kind is presented as a labeled property.');
        assert.equal(layout.clipped, false);
        for (const list of layout.lists) {
            assert.ok(list.padding >= 20 && list.itemInset >= 20, 'List markers have room inside the scroll container.');
            assert.notEqual(list.type, 'none', 'List markers remain enabled.');
        }
    }
    console.log('PASS: Product details Kind placement and list marker space at wide/narrow widths in both theme palettes');
    await evaluate("editor.disabled = true; changeMode(true)");
    assert.equal(await evaluate('document.activeElement === toggle'), true, 'Busy editor still permits exiting fullscreen.');
    await evaluate('cleanupEditor()');
    assert.equal(await evaluate("document.documentElement.style.overflow"), '', 'Disposal restores page scrolling.');
    console.log('PASS: busy fallback focus and disposal cleanup');
    // Field-only editors stay inside their owner's form, with independent controls/state.
    await evaluate(`(async () => {
        const { initializeSplitter, setFullscreen, dispose } = await import('/module.js');
        const form = document.createElement('form'); document.body.append(form);
        window.parentSubmits = 0; form.onsubmit = event => { event.preventDefault(); parentSubmits++; };
        window.fields = [0, 1].map(index => {
            const field = document.createElement('dialog'); field.open = true;
            field.className = 'description-editor-shell field-only';
            field.append(root.querySelector('.description-editor-content').cloneNode(true));
            field.querySelector('textarea').disabled = false;
            field.querySelector('textarea').value = 'Independent draft ' + index;
            form.append(field); initializeSplitter(field);
            field.querySelector('[data-markdown-fullscreen-toggle]').onclick = () => setFullscreen(field, !field.matches(':modal'));
            field.querySelector('#arrangement').onclick = () => field.querySelector('.description-layout').className = 'description-layout stacked';
            field.addEventListener('cancel', event => { event.preventDefault(); setFullscreen(field, false); });
            return field;
        });
        window.disposeFields = () => { fields.forEach(dispose); form.remove(); };
    })()`);
    for (const width of [1280, 390]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height: 800, deviceScaleFactor: 1, mobile: false });
        await evaluate('fields[0].querySelector("textarea").style.height = "510px"');
        await waitFor('Math.abs(fields[0].querySelector(".description-source").getBoundingClientRect().height - fields[0].querySelector(".description-preview").getBoundingClientRect().height) < 1');
        const otherHeight = await evaluate('fields[1].querySelector("textarea").clientHeight');
        await evaluate('fields[0].querySelector("[data-markdown-fullscreen-toggle]").click()');
        await waitFor('fields[0].matches(":modal")');
        assert.equal(await evaluate('fields[0].getBoundingClientRect().width'), width);
        assert.equal(await evaluate('fields[0].getBoundingClientRect().height'), 800);
        assert.ok(await evaluate('fields[0].querySelector(".description-layout").clientHeight > 650'), 'Field-only panes fill fullscreen.');
        await key('Escape', 'Escape', 27);
        await waitFor('!fields[0].matches(":modal")');
        assert.equal(await evaluate('fields[0].querySelector("textarea").value'), 'Independent draft 0');
        assert.equal(await evaluate('fields[1].querySelector("textarea").clientHeight'), otherHeight);
        assert.equal(await evaluate('fields[0].querySelector("textarea").style.height'), '510px');
        assert.equal(await evaluate('document.activeElement === fields[0].querySelector("[data-markdown-fullscreen-toggle]")'), true);
        assert.equal(await evaluate('parentSubmits'), 0, 'Display controls must not submit the owner form.');
        assert.equal(await evaluate('fields[0].querySelector("form")'), null, 'No nested forms.');
    }
    await evaluate('fields[1].querySelector("[data-markdown-fullscreen-toggle]").click(); disposeFields()');
    assert.equal(await evaluate('document.documentElement.style.overflow'), '');
    console.log('PASS: multiple field-only editors, resize/fullscreen geometry, independent drafts, form ownership, focus and disposal');
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
        await evaluate(`const idLabel = [...document.querySelectorAll('label')].find(label => label.textContent.trim().startsWith('Product ID'));
            const idInput = document.getElementById(idLabel.htmlFor); idInput.value = 'EDITOR-BROWSER-001';
            idInput.dispatchEvent(new Event('change', { bubbles: true }));`);
        const kindPosition = await evaluate(`(() => {
            const label = [...document.querySelectorAll('label')].find(label => label.textContent.trim().replace('*','').trim() === 'Kind');
            const input = document.getElementById(label.htmlFor); input.scrollIntoView({block:'center'});
            const rect = input.getBoundingClientRect(); return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
        })()`);
        await command('Input.dispatchMouseEvent', { type: 'mousePressed', ...kindPosition, button: 'left', clickCount: 1 });
        await command('Input.dispatchMouseEvent', { type: 'mouseReleased', ...kindPosition, button: 'left', clickCount: 1 });
        await waitFor('[...document.querySelectorAll(".mud-list-item")].some(item => item.textContent.trim() === "System")');
        await evaluate('[...document.querySelectorAll(".mud-list-item")].find(item => item.textContent.trim() === "System").click()');
        await delay(200);
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
        // Exercise the rendered shared editor on real full-page and embedded-field callers.
        for (const entity of ['features', 'user-needs', 'test-cases']) {
            let editQuery;
            for (const mode of ['create', 'edit']) {
                const route = new URL(`/${entity}/${mode}`, app);
                route.search = mode === 'create' ? new URLSearchParams({ product: productId }).toString() : editQuery;
                await command('Page.navigate', { url: route.href });
                await command('Page.bringToFront');
                await waitFor('Boolean(document.querySelector("[role=separator][aria-valuetext]"))');
                if (mode === 'create') {
                    await evaluate(`(() => {
                        for (const [labelText, value] of [['Title', 'Shared editor browser test'], ['User Need ID', 'UN-EDITOR-001']]) {
                            const label = [...document.querySelectorAll('label')].find(label => label.textContent.trim().startsWith(labelText));
                            if (!label) continue;
                            const input = document.getElementById(label.htmlFor); input.value = value;
                            input.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                    })()`);
                    if (entity === 'test-cases') {
                        await evaluate('[...document.querySelectorAll("button")].find(button => button.textContent.trim() === "Add step").click()');
                        await waitFor('document.querySelectorAll(".description-editor-shell").length === 4');
                        await waitFor('document.querySelectorAll("[role=separator][aria-valuetext]").length === 4');
                    }
                }
                const count = await evaluate('document.querySelectorAll(".description-editor-shell").length');
                assert.equal(count, entity === 'test-cases' ? 4 : 1);
                for (let index = 0; index < count; index++) {
                    const draft = `## ${entity} ${mode} field ${index}`;
                    await evaluate(`window.activeShell = document.querySelectorAll('.description-editor-shell')[${index}];
                        window.activeTextarea = activeShell.querySelector('textarea');
                        activeTextarea.value = ${JSON.stringify(draft)};
                        activeTextarea.dispatchEvent(new Event('input', { bubbles: true }));`);
                    await delay(200); // Allow the server-side binding/preview render to finish before resizing.
                    await evaluate('activeTextarea.style.height = "530px"');
                    await waitFor('Math.abs(activeShell.querySelector(".description-source").getBoundingClientRect().height - activeShell.querySelector(".description-preview").getBoundingClientRect().height) < 1');
                    await evaluate('activeShell.querySelector("[aria-label=Stacked]").click()');
                    await waitFor('Boolean(activeShell.querySelector(".description-layout.stacked"))');
                    await evaluate('activeShell.querySelector("[data-markdown-fullscreen-toggle]").click()');
                    await waitFor('activeShell.matches(":modal")');
                    assert.deepEqual(await evaluate('({width: activeShell.getBoundingClientRect().width, height: activeShell.getBoundingClientRect().height})'), { width: 1280, height: 800 });
                    assert.ok(await evaluate('activeShell.querySelector(".description-layout").getBoundingClientRect().height > 400'), 'Actual panes expand inside the fullscreen shell.');
                    await key('Escape', 'Escape', 27);
                    await waitFor('!activeShell.matches(":modal") && activeShell.querySelector("[data-markdown-fullscreen-toggle]").getAttribute("aria-busy")?.toLowerCase() !== "true"');
                    assert.equal(await evaluate('activeTextarea === activeShell.querySelector("textarea")'), true);
                    assert.equal(await evaluate('activeTextarea.value'), draft);
                    assert.equal(await evaluate('activeTextarea.style.height'), '530px');
                }
                await delay(200); // Finish the fullscreen-exit render before submitting the owner form.
                const formId = { features: 'feature-form', 'user-needs': 'user-need-form', 'test-cases': 'test-case-form' }[entity];
                await evaluate(`document.getElementById(${JSON.stringify(formId)}).requestSubmit()`);
                await waitFor(`location.pathname === '/${entity}/details'`);
                editQuery = await evaluate('location.search');
                await waitFor(`document.body.textContent.includes('${entity} ${mode} field 0')`);
                assert.equal(await evaluate('document.documentElement.style.overflow'), '');
                console.log(`PASS: actual Blazor ${entity} ${mode}: all ${count} Markdown fields resize/orient/fullscreen, retain drafts and save`);
            }
        }
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
