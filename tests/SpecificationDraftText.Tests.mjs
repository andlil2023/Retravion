// node --test tests/SpecificationDraftText.Tests.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readMarkdownInput } from '../src/Retravion.Web/wwwroot/js/markdownFullscreen.js';
import { streamSpecificationHeading } from '../src/Retravion.Web/wwwroot/js/specificationDraft.js';

// Exercise the browser module and TextEncoder, substituting only Blazor's reference wrapper.
test('Draft text rejects unmatched surrogates without creating a stream or changing the inputs', t => {
    globalThis.DotNet = { createJSStreamReference: t.mock.fn(bytes => bytes) };
    t.after(() => delete globalThis.DotNet);
    const valid = { value: 'Other field 😃' };
    for (const value of ['before\uD800after', 'before\uDC00after', '\uD800😃', '😃\uDC00']) {
        const input = { value };
        assert.deepEqual(readMarkdownInput(input, 1024), { invalidUnicode: true, content: null });
        assert.deepEqual(streamSpecificationHeading(input, valid), { invalidField: 'id', content: null });
        assert.deepEqual(streamSpecificationHeading(valid, input), { invalidField: 'title', content: null });
        assert.equal(input.value, value);
    }
    assert.equal(DotNet.createJSStreamReference.mock.callCount(), 0);
});

test('Draft text encodes long values, valid pairs and the literal replacement character losslessly', t => {
    globalThis.DotNet = { createJSStreamReference: bytes => bytes };
    t.after(() => delete globalThis.DotNet);
    const id = '  DRAFT-😃  ';
    const title = 'A'.repeat(40 * 1024) + '�😃';
    const heading = streamSpecificationHeading({ value: id }, { value: title });
    const decoder = new TextDecoder('utf-8', { fatal: true });
    assert.deepEqual(JSON.parse(decoder.decode(heading.content)), { id, title });
    const value = '**😃�**\nLine two';
    const size = new TextEncoder().encode(value).length;
    assert.equal(decoder.decode(readMarkdownInput({ value }, size).content), value);
    assert.deepEqual(readMarkdownInput({ value }, size - 1), { tooLarge: true, content: null });
});
