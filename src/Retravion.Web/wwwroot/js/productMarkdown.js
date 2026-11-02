// Only trusted bootstrap URLs enter srcdoc. Submitted Markdown travels via postMessage,
// never through HTML interpolation or the application DOM's HTML parser.
const frameScript = new URL('./mermaidFrame.js', import.meta.url).href;
const libraryScript = new URL('../lib/mermaid/mermaid.min.js', import.meta.url).href;
const attribute = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const frameDocument = `<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${attribute(`default-src 'none'; script-src ${frameScript} ${libraryScript}; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'`)}">
<style>body{margin:0;font-family:system-ui,sans-serif}svg{max-width:100%;height:auto}#diagram{overflow:auto}</style>
<script src="${attribute(libraryScript)}"></script>
<script src="${attribute(frameScript)}" defer></script>
</head><body><div id="diagram"></div></body></html>`;

async function renderBlock(root, code) {
    if (!root.isConnected) return;
    const pre = code.parentElement;
    const frame = document.createElement('iframe');
    frame.className = 'product-mermaid';
    frame.title = 'Mermaid diagram';
    frame.setAttribute('sandbox', 'allow-scripts'); // Deliberately no allow-same-origin.
    frame.setAttribute('referrerpolicy', 'no-referrer');
    frame.style.cssText = 'width:100%;height:200px;border:0;visibility:hidden';
    const id = crypto.randomUUID();
    await new Promise(resolve => {
        const finish = success => {
            clearTimeout(timeout);
            observer.disconnect();
            window.removeEventListener('message', receive);
            if (!root.isConnected) { frame.remove(); resolve(); return; }
            if (success) {
                frame.style.visibility = 'visible';
                pre.remove();
            } else {
                frame.remove();
                const message = document.createElement('p');
                message.setAttribute('role', 'status');
                message.textContent = 'Diagram could not be rendered. Check the Mermaid syntax.';
                pre.after(message);
            }
            resolve();
        };
        const receive = event => {
            if (event.source !== frame.contentWindow || event.origin !== 'null') return;
            const data = event.data;
            if (data?.type === 'mermaid-ready') {
                // '*' is necessary for this opaque-origin frame; source is explicitly bound above.
                frame.contentWindow.postMessage({ type: 'mermaid-render', id, source: code.textContent }, '*');
            } else if (data?.type === 'mermaid-result' && data.id === id) {
                const validHeight = Number.isFinite(data.height) && data.height > 0;
                if (validHeight) frame.style.height = `${Math.max(64, Math.min(4096, data.height))}px`;
                finish(data.success === true && validHeight);
            }
        };
        const observer = new MutationObserver(() => { if (!root.isConnected) finish(false); });
        const timeout = setTimeout(() => finish(false), 15000);
        observer.observe(document, { childList: true, subtree: true });
        window.addEventListener('message', receive);
        frame.srcdoc = frameDocument;
        pre.after(frame);
    });
}

export async function render(root) {
    const blocks = [...root.querySelectorAll('pre > code.language-mermaid')];
    await Promise.all(blocks.map(code => renderBlock(root, code)));
}
