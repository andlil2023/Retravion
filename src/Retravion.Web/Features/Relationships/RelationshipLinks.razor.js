// Keep the exact starting record visible without shrinking text or scrolling the page.
export function centerFocus(viewport) {
    const focused = viewport?.querySelector('[data-focus="true"]');
    if (!focused) return;
    const box = focused.getBoundingClientRect();
    const frame = viewport.getBoundingClientRect();
    viewport.scrollLeft += box.left - frame.left + box.width / 2 - viewport.clientWidth / 2;
    viewport.scrollTop += box.top - frame.top + box.height / 2 - viewport.clientHeight / 2;
}
