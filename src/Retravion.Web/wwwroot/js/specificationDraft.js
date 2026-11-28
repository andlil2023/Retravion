import { hasUnpairedSurrogate } from './markdownFullscreen.js';

export function streamSpecificationHeading(id, title) {
    const heading = { id: id.value, title: title.value };
    for (const field of ['id', 'title']) {
        if (hasUnpairedSurrogate(heading[field])) return { invalidField: field, content: null };
    }
    const bytes = new TextEncoder().encode(JSON.stringify(heading));
    return { invalidField: null, content: DotNet.createJSStreamReference(bytes) };
}
