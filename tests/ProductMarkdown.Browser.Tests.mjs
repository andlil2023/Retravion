// No packages: node tests/ProductMarkdown.Browser.Tests.mjs
// Real Chromium, production Mermaid bundle/module and sandbox, no CDN.
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
assert.ok(executable, 'Set RETRAVION_TEST_BROWSER to an installed Chromium executable.');
const files = new Map();
for (const path of ['js/productMarkdown.js', 'js/mermaidFrame.js', 'js/markdownFullscreen.js', 'lib/mermaid/mermaid.min.js'])
    files.set('/' + path, await readFile(new URL('../src/Retravion.Web/wwwroot/' + path, import.meta.url)));
const editorCss = (await readFile(new URL('../src/Retravion.Web/Components/Markdown/DescriptionEditor.razor.css', import.meta.url), 'utf8')).replaceAll('::deep', '');
const requests = [];
const server = createServer((request, response) => {
    requests.push(request.url);
    response.setHeader('Content-Type', request.url.endsWith('.js') ? 'text/javascript' : 'text/html');
    if (files.has(request.url)) return response.end(files.get(request.url));
    if (request.url !== '/') { response.statusCode = 404; return response.end(); }
    response.end(`<!doctype html><html><head><meta charset="utf-8"><style>
        :root { --mud-palette-background: white; --mud-palette-text-primary: black; --mud-palette-divider: gray; }
        body { margin:0; } ${editorCss}
        </style></head><body>
        <dialog open class="description-editor-shell"><form>
            <div class="description-toolbar"><h2>Description</h2><button type="button" data-markdown-fullscreen-toggle>Fullscreen</button></div>
            <div class="description-layout side-by-side">
                <div class="description-source"><label for="source">Description (Markdown)</label><textarea id="source" rows="12"></textarea></div>
                <div class="description-splitter" role="separator" tabindex="0" aria-valuenow="50"></div>
                <section class="description-preview"><h3>Preview</h3><main id="preview"></main></section>
            </div>
        </form><div class="product-actions"><button id="save">Save</button></div></dialog>
        <script type="module">
        import { render } from '/js/productMarkdown.js';
        import { initializeSplitter, setFullscreen } from '/js/markdownFullscreen.js';
        initializeSplitter(document.querySelector('dialog'));
        window.setEditorFullscreen = enabled => setFullscreen(document.querySelector('dialog'), enabled);
        window.renderMarkdown = render;
        window.makePreview = source => {
            const root = document.createElement('div');
            const pre = document.createElement('pre');
            const code = document.createElement('code');
            code.className = 'language-mermaid'; code.textContent = source;
            pre.append(code); root.append(pre); document.querySelector('main').replaceChildren(root);
            return root;
        };
        document.querySelector('textarea').addEventListener('input', event => {
            window.currentRoot = makePreview(event.target.value);
            window.pendingRender = render(currentRoot);
        });
        window.ready = true;
        </script></body></html>`);
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const origin = `http://127.0.0.1:${server.address().port}`;
const profile = await mkdtemp(join(tmpdir(), 'retravion-markdown-browser-'));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser, socket, nextId = 0;
const pending = new Map(), contexts = new Map();
async function command(method, params = {}, sessionId) {
    const id = ++nextId;
    const result = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`Browser command timed out: ${method}`)); }, 30000);
        pending.set(id, {
            resolve: value => { clearTimeout(timeout); resolve(value); },
            reject: error => { clearTimeout(timeout); reject(error); }
        });
    });
    socket.send(JSON.stringify({ id, method, params, sessionId }));
    return result;
}
async function evaluate(expression, sessionId, contextId) {
    const response = await command('Runtime.evaluate', { expression, contextId, awaitPromise: true, returnByValue: true, replMode: true, allowUnsafeEvalBlockedByCSP: false }, sessionId);
    assert.ok(!response.exceptionDetails, JSON.stringify(response.exceptionDetails));
    return response.result.value;
}
async function frameEvaluate(expression) {
    expression = `await (${expression})`;
    // Opaque-origin frames can be in a separate Chromium renderer process.
    const { targetInfos } = await command('Target.getTargets');
    const frameTarget = targetInfos.find(target => target.type === 'iframe' && target.url === 'about:srcdoc');
    if (frameTarget) {
        const { sessionId } = await command('Target.attachToTarget', { targetId: frameTarget.targetId, flatten: true });
        try { return await evaluate(expression, sessionId); }
        finally { await command('Target.detachFromTarget', { sessionId }); }
    }
    const { frameTree } = await command('Page.getFrameTree');
    const frameId = frameTree.childFrames?.[0]?.frame.id;
    const context = [...contexts.values()].find(context => context.auxData?.frameId === frameId && context.auxData?.isDefault);
    assert.ok(context, 'The actual diagram frame has an execution context.');
    return evaluate(expression, undefined, context.id);
}
async function preview(source) {
    await evaluate(`window.currentRoot = makePreview(${JSON.stringify(source)}); await renderMarkdown(currentRoot);`);
}
async function assertStableTyping(source, scenario) {
    await command('Page.bringToFront');
    await evaluate(`window.editor = document.querySelector('textarea'); editor.focus();
        editor.setSelectionRange(editor.value.indexOf('Draft') + 5, editor.value.indexOf('Draft') + 5);`);
    // Let focus/validation and fullscreen transitions settle before measuring typing.
    await delay(300);
    await evaluate(`window.geometry = () => {
            const rect = editor.getBoundingClientRect();
            return [rect.x, rect.y, rect.width, rect.height,
                document.querySelector('.description-source').getBoundingClientRect().height,
                document.querySelector('.description-preview').getBoundingClientRect().height,
                document.querySelector('.product-actions').getBoundingClientRect().top];
        };
        window.samples = [geometry()]; window.sampling = true;
        requestAnimationFrame(function sample() { samples.push(geometry()); if (sampling) requestAnimationFrame(sample); });`);
    for (const character of ' updated') {
        await command('Input.insertText', { text: character });
        await delay(40);
    }
    await evaluate('if (window.pendingRender) await pendingRender;');
    await waitFor('document.querySelector(".description-preview iframe")?.style.visibility === "visible"');
    await delay(50);
    await evaluate('sampling = false; samples.push(geometry());');
    const samples = await evaluate('samples');
    assert.ok(samples.length > 2, 'Geometry was sampled during typing, not only before/after.');
    for (const sample of samples)
        for (let index = 0; index < sample.length; index++)
            assert.ok(Math.abs(sample[index] - samples[0][index]) < 1,
                `Stable editor geometry ${scenario}: ${JSON.stringify(samples[0])} -> ${JSON.stringify(sample)}`);
    assert.equal(await evaluate('document.activeElement === editor'), true);
    assert.equal(await evaluate('editor === document.querySelector("textarea")'), true);
    assert.equal(await evaluate('editor.value'), source.replace('Draft', 'Draft updated'));
    assert.equal(await evaluate('editor.selectionStart'), source.indexOf('Draft') + 'Draft updated'.length);
    assert.equal(await frameEvaluate('document.body.textContent.includes("Draft updated")'), true, 'Latest diagram still renders.');
    assert.equal(await evaluate('document.querySelector(".description-preview").scrollHeight > document.querySelector(".description-preview").clientHeight'), true, 'Tall diagrams remain reachable by scrolling.');
    console.log(`PASS: stable geometry during real typing, draft/caret/focus and tall-diagram preview ${scenario}`);
}
async function waitFor(expression) {
    for (let attempt = 0; attempt < 400; attempt++) {
        if (await evaluate(expression)) return;
        await delay(50);
    }
    assert.fail(`Timed out: ${expression}`);
}
try {
    browser = spawn(executable, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
        '--no-first-run', '--no-default-browser-check', 'about:blank'], { stdio: 'ignore' });
    browser.on('error', error => console.error(error.message));
    let port;
    for (let attempt = 0; attempt < 100; attempt++) {
        try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; }
        catch { await delay(100); }
    }
    assert.ok(port, 'Browser did not expose its test debugging port.');
    const page = await (await fetch(`http://127.0.0.1:${port}/json/new?${origin}`, { method: 'PUT' })).json();
    socket = new WebSocket(page.webSocketDebuggerUrl);
    socket.addEventListener('message', event => {
        const message = JSON.parse(event.data), item = pending.get(message.id);
        if (message.method === 'Runtime.executionContextCreated' && !message.sessionId)
            contexts.set(message.params.context.id, message.params.context);
        if (message.method === 'Runtime.executionContextDestroyed' && !message.sessionId)
            contexts.delete(message.params.executionContextId);
        if (!item) return;
        pending.delete(message.id);
        if (message.error) item.reject(new Error(JSON.stringify(message.error)));
        else item.resolve(message.result);
    });
    await once(socket, 'open');
    await command('Runtime.enable');
    await command('Page.enable');
    for (let attempt = 0; attempt < 100 && !await evaluate('Boolean(window.ready)'); attempt++) await delay(50);
    assert.equal(await evaluate('Boolean(window.ready)'), true);
    await evaluate('renderMarkdown(document.querySelector("main"))');
    assert.equal(requests.includes('/lib/mermaid/mermaid.min.js'), false, 'Ordinary Markdown does not load Mermaid.');
    await evaluate(String.raw`window.firstRoot = makePreview('flowchart LR\nA[Start] --> B[Finish]'); window.firstRender = renderMarkdown(firstRoot);`);
    await evaluate(String.raw`window.currentRoot = makePreview('sequenceDiagram\nAlice->>Bob: Latest draft'); await Promise.all([firstRender, renderMarkdown(currentRoot)]);`);
    assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true);
    assert.equal(await frameEvaluate('document.body.textContent.includes("Latest draft")'), true);
    assert.equal(await evaluate('Boolean(firstRoot.querySelector("iframe"))'), false, 'Detached draft is cleaned up.');
    await preview('flowchart LR\nA[Start] --> B[Finish]');
    assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true);
    assert.equal(await frameEvaluate('document.body.textContent.includes("Start") && document.body.textContent.includes("Finish")'), true);
    assert.equal(await evaluate('typeof window.mermaid'), 'undefined', 'Mermaid never loads in the application page.');
    assert.equal(await evaluate('currentRoot.querySelector("iframe").contentDocument'), null, 'Parent cannot read the opaque-origin diagram.');
    assert.equal(await evaluate('currentRoot.querySelector("iframe").getAttribute("sandbox")'), 'allow-scripts');
    assert.ok(await evaluate('parseFloat(currentRoot.querySelector("iframe").style.height) >= 64'));
    console.log('PASS: lazy local loading, actual flowchart/sequence SVG, live updates and stale-draft cleanup');

    for (const source of [
        'flowchart LR\nA["<img src=x onerror=window.__retravionXssProbe=true>"] --> B[Safe]\nclick B "javascript:parent.__retravionXssProbe=true"',
        '%%{init: {"securityLevel": "loose", "htmlLabels": true}}%%\nflowchart LR\nA[Click]\nclick A "javascript:parent.__retravionXssProbe=true"',
        '---\nconfig:\n  securityLevel: loose\n  htmlLabels: true\n---\nflowchart LR\nA --> B'
    ]) {
        await preview(source);
        assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true);
        assert.equal(await frameEvaluate('Boolean(document.querySelector("a[href], a[*|href]"))'), false);
        assert.equal(await frameEvaluate('Boolean(window.__retravionXssProbe)'), false);
        assert.equal(await evaluate('Boolean(window.__retravionXssProbe)'), false);
    }
    assert.equal(await frameEvaluate('(() => { try { parent.document.body.dataset.compromised = "yes"; return false; } catch { return true; } })()'), true);
    assert.equal(await frameEvaluate('(() => { try { document.cookie; return false; } catch { return true; } })()'), true);
    assert.equal(await frameEvaluate('(() => { try { localStorage.getItem("test"); return false; } catch { return true; } })()'), true);
    assert.equal(await frameEvaluate('window.open("about:blank") === null'), true);
    assert.equal(await frameEvaluate(`(() => { try { top.location = ${JSON.stringify(origin + '/security-navigation')}; return false; } catch { return true; } })()`), true);
    assert.equal(await frameEvaluate(`(async () => { try { await fetch(${JSON.stringify(origin + '/security-fetch')}); return false; } catch { return true; } })()`), true);
    assert.equal(await frameEvaluate('(() => { try { new Function("return 1")(); return false; } catch { return true; } })()'), true, 'CSP blocks eval.');
    await frameEvaluate(`(() => {
        const image = new Image(); image.src = ${JSON.stringify(origin + '/security-image')}; document.body.append(image);
        const script = document.createElement('script'); script.src = ${JSON.stringify(origin + '/security-script.js')}; document.head.append(script);
        const form = document.createElement('form'); form.action = ${JSON.stringify(origin + '/security-form')}; form.method = 'POST'; document.body.append(form); form.submit();
        const inline = document.createElement('script'); inline.textContent = 'window.__retravionXssProbe=true'; document.head.append(inline);
        const styled = document.createElement('div'); styled.style.backgroundImage = ${JSON.stringify(`url(${origin}/security-style)`)}; document.body.append(styled);
    })()`);
    await delay(300);
    assert.equal(requests.some(path => path.startsWith('/security-')), false, 'No blocked request reaches the server.');
    assert.equal(await frameEvaluate('Boolean(window.__retravionXssProbe)'), false, 'CSP blocks inline script.');
    assert.equal(await evaluate('location.pathname'), '/');
    assert.equal(await evaluate('Boolean(document.body.dataset.compromised)'), false);
    // Forged results from another source must not change parent DOM/height.
    const height = await evaluate('currentRoot.querySelector("iframe").style.height');
    await evaluate('window.postMessage({type:"mermaid-result",success:true,height:999999,id:"fake"}, "*")');
    await delay(50);
    assert.equal(await evaluate('currentRoot.querySelector("iframe").style.height'), height);
    console.log('PASS: hostile labels/directives, parent/cookie/storage isolation, CSP network/inline/eval restrictions, navigation/pop-up/form blocking and message binding');

    await preview('not valid Mermaid <script>parent.__retravionXssProbe=true</script>');
    assert.equal(await evaluate('currentRoot.querySelector("code").textContent.includes("not valid Mermaid")'), true);
    assert.equal(await evaluate('currentRoot.querySelector("[role=status]").textContent'), 'Diagram could not be rendered. Check the Mermaid syntax.');
    assert.equal(await evaluate('Boolean(currentRoot.querySelector("iframe, svg, script"))'), false);
    await preview('flowchart LR\nA --> B');
    assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true, 'Valid input recovers after syntax error.');
    assert.equal(await evaluate('Boolean(window.__retravionXssProbe)'), false);
    console.log('PASS: syntax errors retain escaped source/generic feedback, next valid draft recovers');
    // Sample every animation frame, including pending iframe layout, not just final geometry.
    for (const [width, arrangement, fullscreen] of [
        [1280, 'side-by-side', false], [1280, 'stacked', false],
        [1280, 'side-by-side', true], [1280, 'stacked', true],
        [390, 'side-by-side', false], [390, 'side-by-side', true]
    ]) {
        await command('Emulation.setDeviceMetricsOverride', { width, height: 800, deviceScaleFactor: 1, mobile: false });
        await evaluate(`setEditorFullscreen(${fullscreen});
            document.querySelector('.description-layout').className = ${JSON.stringify('description-layout ' + arrangement)};`);
        const source = 'flowchart TD\nA[Draft] --> B --> C --> D --> E --> F --> G --> H --> I --> J';
        await preview(source);
        await evaluate(`document.querySelector('textarea').value = ${JSON.stringify(source)};`);
        await assertStableTyping(source, `at ${width}px ${arrangement}, fullscreen=${fullscreen}`);
    }
    await evaluate('setEditorFullscreen(false)');
    // Optional end-to-end check against a separately started disposable app/database.
    if (process.env.RETRAVION_MARKDOWN_APP_URL) {
        const app = new URL(process.env.RETRAVION_MARKDOWN_APP_URL);
        const username = process.env.RETRAVION_MARKDOWN_USERNAME;
        const password = process.env.RETRAVION_MARKDOWN_PASSWORD;
        assert.ok(username && password, 'Supply disposable-app login credentials.');
        await command('Page.navigate', { url: app.href });
        await waitFor('Boolean(document.querySelector("input[name=username]"))');
        await evaluate(`await (async () => {
            const token = html => new DOMParser().parseFromString(html, 'text/html').querySelector('input[name=__RequestVerificationToken]').value;
            const login = await fetch('/identity/login', { method: 'POST', body: new URLSearchParams({
                username: ${JSON.stringify(username)}, password: ${JSON.stringify(password)},
                __RequestVerificationToken: token(await (await fetch('/')).text()) }) });
            if (new URL(login.url).pathname === '/password-replacement') {
                const replacement = 'MarkdownReplacement123!';
                await fetch('/identity/password-replacement', { method: 'POST', body: new URLSearchParams({
                    currentPassword: ${JSON.stringify(password)}, newPassword: replacement, confirmNewPassword: replacement,
                    __RequestVerificationToken: token(await login.text()) }) });
            }
        })()`);
        await command('Page.navigate', { url: new URL('/products/create', app).href });
        await waitFor('Boolean(document.querySelector("[role=separator][aria-valuetext]"))');
        const tallSource = '```mermaid\nflowchart TD\nA[Draft] --> B --> C --> D --> E --> F --> G --> H --> I --> J\n```';
        for (const [width, arrangement, fullscreen] of [
            [1280, 'Side by side', false], [1280, 'Stacked', false],
            [1280, 'Side by side', true], [1280, 'Stacked', true],
            [390, 'Side by side', false], [390, 'Side by side', true]
        ]) {
            await command('Emulation.setDeviceMetricsOverride', { width, height: 800, deviceScaleFactor: 1, mobile: false });
            await evaluate(`if (document.querySelector('dialog').matches(':modal')) document.querySelector('[data-markdown-fullscreen-toggle]').click();`);
            await waitFor('!document.querySelector("dialog").matches(":modal")');
            await evaluate(`document.querySelector('[aria-label="${arrangement}"]').click();`);
            await waitFor(`Boolean(document.querySelector('.description-layout.${arrangement === 'Stacked' ? 'stacked' : 'side-by-side'}'))`);
            if (fullscreen) {
                await evaluate('document.querySelector("[data-markdown-fullscreen-toggle]").click()');
                await waitFor('document.querySelector("dialog").matches(":modal")');
            }
            await evaluate(`const input = document.querySelector('textarea'); input.value = ${JSON.stringify(tallSource)};
                input.dispatchEvent(new Event('input', { bubbles: true }));`);
            await waitFor('document.querySelector(".description-preview iframe")?.style.visibility === "visible"');
            await assertStableTyping(tallSource, `in actual Blazor at ${width}px ${arrangement}, fullscreen=${fullscreen}`);
        }
        await evaluate('document.querySelector("[data-markdown-fullscreen-toggle]").click()');
        await waitFor('!document.querySelector("dialog").matches(":modal")');
        await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
        const markdown = 'Before paragraph.\n\n| Name | Status |\n| --- | --- |\n| Product | Active |\n\n```mermaid\nflowchart LR\nA[Draft] --> B[Reviewed]\n```\n\nAfter paragraph.';
        await evaluate(`const textarea = document.querySelector('textarea'); textarea.value = ${JSON.stringify(markdown)};
            textarea.dispatchEvent(new Event('input', { bubbles: true }));`);
        await waitFor('Boolean(document.querySelector(".description-preview table")) && document.querySelector(".description-preview iframe")?.style.visibility === "visible"');
        assert.equal(await frameEvaluate('Boolean(document.querySelector("svg")) && document.body.textContent.includes("Reviewed")'), true);
        assert.equal(await evaluate('document.querySelector(".description-preview").textContent.includes("Before paragraph.")'), true);
        await evaluate('document.querySelector("[aria-label=Stacked]").click()');
        await waitFor('Boolean(document.querySelector(".description-layout.stacked"))');
        await evaluate(`for (const [name, value] of [['Product ID', 'MD-BROWSER-' + Date.now()], ['Name', 'Markdown browser example']]) {
            const label = [...document.querySelectorAll('label')].find(label => label.textContent.trim().replace('*','').trim() === name);
            const input = document.getElementById(label.htmlFor); input.value = value;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }`);
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
        await evaluate('document.querySelector("[data-markdown-fullscreen-toggle]").click()');
        await waitFor('document.querySelector("dialog").matches(":modal")');
        assert.equal(await evaluate('Boolean(document.querySelector(".description-preview table"))'), true);
        assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true);
        await evaluate('document.querySelector("dialog form").requestSubmit()');
        await waitFor('location.pathname === "/products/details"');
        await waitFor('Boolean(document.querySelector(".product-markdown table")) && document.querySelector(".product-markdown iframe")?.style.visibility === "visible"');
        assert.equal(await frameEvaluate('Boolean(document.querySelector("svg")) && document.body.textContent.includes("Reviewed")'), true);
        assert.equal(await evaluate('document.querySelector(".product-markdown").textContent.includes("After paragraph.")'), true);
        if (process.env.RETRAVION_MARKDOWN_SCREENSHOT) {
            await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 1000, deviceScaleFactor: 1, mobile: false });
            const { data } = await command('Page.captureScreenshot', { format: 'png' });
            await writeFile(process.env.RETRAVION_MARKDOWN_SCREENSHOT, Buffer.from(data, 'base64'));
        }
        const productId = await evaluate('new URL(location.href).searchParams.get("id")');
        const edit = new URL('/products/description', app); edit.searchParams.set('id', productId);
        await command('Page.navigate', { url: edit.href });
        await waitFor('Boolean(document.querySelector("[role=separator][aria-valuetext]"))');
        assert.equal(await evaluate('document.querySelector("textarea").value'), markdown, 'Saved Markdown source remains unchanged.');
        await waitFor('document.querySelector(".description-preview iframe")?.style.visibility === "visible"');
        assert.equal(await frameEvaluate('Boolean(document.querySelector("svg"))'), true);
        console.log('PASS: actual Blazor live table/paragraph/diagram preview, stacked/fullscreen, create/save/details and source-preserving reload');
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
