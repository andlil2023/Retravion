// Runs only in an opaque-origin sandbox. No submitted content or generated markup
// is sent back to the application; the parent accepts only status and bounded height.
(() => {
    let started = false;
    window.addEventListener('message', async event => {
        const data = event.data;
        if (event.source !== parent || started || data?.type !== 'mermaid-render' ||
            typeof data.id !== 'string' || typeof data.source !== 'string') return;
        started = true;
        try {
            if (data.source.length > 50000) throw new Error('Diagram too large.');
            mermaid.initialize({
                startOnLoad: false,
                securityLevel: 'strict',
                suppressErrorRendering: true,
                htmlLabels: false,
                flowchart: { htmlLabels: false },
                maxTextSize: 50000,
                secure: ['secure', 'securityLevel', 'startOnLoad', 'suppressErrorRendering', 'maxTextSize', 'htmlLabels', 'flowchart']
            });
            const { svg } = await mermaid.render('mermaid-diagram', data.source);
            const diagram = document.getElementById('diagram');
            // Parse inertly and strip navigation before displaying the generated SVG.
            const template = document.createElement('template');
            template.innerHTML = svg;
            for (const link of template.content.querySelectorAll('a')) {
                link.removeAttribute('href');
                link.removeAttribute('xlink:href');
                link.removeAttribute('target');
            }
            diagram.replaceChildren(template.content);
            parent.postMessage({ type: 'mermaid-result', id: data.id, success: true,
                height: Math.ceil(diagram.getBoundingClientRect().height) }, '*');
        } catch {
            // Parser errors can contain submitted values: send/log no exception details.
            parent.postMessage({ type: 'mermaid-result', id: data.id, success: false }, '*');
        }
    });
    parent.postMessage({ type: 'mermaid-ready' }, '*');
})();
