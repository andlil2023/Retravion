# Mermaid browser bundle

- Version: **11.17.2** (MIT; see `LICENSE` and bundled dependency notices).
- Source: https://registry.npmjs.org/mermaid/-/mermaid-11.17.2.tgz
- Unmodified `package/dist/mermaid.min.js` from the published npm archive.
- SHA-256: `581ed7d74bd9048d0e3a91363927d72ef22942d7722546b27f7cc29e35390eb8`

Served locally; no CDN or runtime package download. Loaded on demand by
`wwwroot/js/productMarkdown.js`, exclusively inside an opaque-origin iframe with
`sandbox="allow-scripts"` (no same-origin, pop-up, form, or top-navigation permissions).
A restrictive frame CSP allows only the pinned library and trusted bootstrap scripts;
network requests and inline/evaluated scripts are blocked. Submitted Markdown travels
via source-bound messages; generated SVG stays in the frame. Remote images and diagram
links are intentionally unavailable. Oversized/invalid diagrams retain their escaped
source with generic feedback. Rendering has a 15-second loading timeout; it is not a
CPU-execution limit for the third-party renderer.

When updating, replace the bundle and license, update the version/hash here, and run:

```sh
node tests/ProductMarkdown.Browser.Tests.mjs
```

The browser tests cover real SVG output, parent/storage isolation, network and script
restrictions, navigation, directives, error recovery, and detached preview cleanup.
