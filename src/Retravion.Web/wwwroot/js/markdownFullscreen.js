// Keep the same dialog/textarea mounted. Native showModal supplies top-layer
// rendering, focus containment and background inertness; this is not the browser Fullscreen API.
const sessions = new WeakMap();
const splitters = new WeakMap();

function selection(editor) {
    return editor && {
        start: editor.selectionStart, end: editor.selectionEnd,
        direction: editor.selectionDirection, top: editor.scrollTop, left: editor.scrollLeft
    };
}

function restoreSelection(editor, saved) {
    if (!editor || !saved) return;
    editor.setSelectionRange(saved.start, saved.end, saved.direction);
    editor.scrollTop = saved.top;
    editor.scrollLeft = saved.left;
}

function finish(dialog, reopen, restoreFocus) {
    const session = sessions.get(dialog);
    if (!session) return;
    sessions.delete(dialog);
    const editor = dialog.querySelector("textarea");
    const saved = selection(editor);
    if (dialog.open) dialog.close();
    dialog.classList.remove("is-fullscreen");
    dialog.setAttribute("role", "group");
    dialog.removeAttribute("aria-modal");
    session.style.setProperty("overflow", session.overflow, session.priority);
    // Restoring open directly keeps the normal inline dialog from autofocus/scrolling.
    if (reopen && dialog.isConnected) dialog.open = true;
    if (restoreFocus && dialog.isConnected)
        dialog.querySelector("[data-markdown-fullscreen-toggle]")?.focus({ preventScroll: true });
    restoreSelection(editor, saved);
}

export function setFullscreen(dialog, enabled) {
    if (!dialog?.isConnected) return;
    if (!enabled) {
        finish(dialog, true, true);
        return;
    }
    if (sessions.has(dialog)) return;
    const editor = dialog.querySelector("textarea");
    const saved = selection(editor);
    const style = dialog.ownerDocument.documentElement.style;
    sessions.set(dialog, { style, overflow: style.getPropertyValue("overflow"), priority: style.getPropertyPriority("overflow") });
    try {
        // An inline non-modal dialog must close before it can enter the modal top layer.
        dialog.close();
        dialog.classList.add("is-fullscreen");
        dialog.setAttribute("role", "dialog");
        dialog.setAttribute("aria-modal", "true");
        dialog.showModal();
        style.setProperty("overflow", "hidden");
        const target = editor && !editor.disabled ? editor : dialog.querySelector("[data-markdown-fullscreen-toggle]");
        target?.focus({ preventScroll: true });
        restoreSelection(editor, saved);
    } catch (error) {
        finish(dialog, true, true);
        restoreSelection(editor, saved);
        throw error;
    }
}

// Keep high-frequency pointer movement local; resizing never touches the Markdown or Blazor form state.
export function initializeSplitter(dialog) {
    if (!dialog?.isConnected || splitters.has(dialog)) return;
    const layout = dialog.querySelector('.description-layout');
    const handle = layout?.querySelector('.description-splitter');
    if (!handle) return;
    const controller = new AbortController();
    const options = { signal: controller.signal };
    let pointer = null;
    let share = 50;
    const active = () => layout.classList.contains('side-by-side') && handle.getClientRects().length > 0;
    const update = value => {
        share = Math.round(Math.min(75, Math.max(25, value)));
        layout.style.setProperty('--markdown-source-width', `${share}fr`);
        layout.style.setProperty('--markdown-preview-width', `${100 - share}fr`);
        handle.setAttribute('aria-valuenow', String(share));
        handle.setAttribute('aria-valuetext', `Markdown ${share}%, preview ${100 - share}%`);
    };
    const move = event => {
        if (!active()) return;
        const bounds = layout.getBoundingClientRect();
        const handleWidth = handle.getBoundingClientRect().width;
        const available = bounds.width - handleWidth;
        if (available > 0) update((event.clientX - bounds.left - handleWidth / 2) / available * 100);
    };
    handle.addEventListener('pointerdown', event => {
        if (!active() || !event.isPrimary || event.button !== 0 || pointer !== null) return;
        event.preventDefault();
        pointer = event.pointerId;
        handle.focus({ preventScroll: true });
        handle.setPointerCapture(pointer);
    }, options);
    handle.addEventListener('pointermove', event => {
        if (event.pointerId === pointer) move(event);
    }, options);
    const release = event => {
        if (event.pointerId !== pointer) return;
        if (handle.hasPointerCapture(pointer)) handle.releasePointerCapture(pointer);
        pointer = null;
    };
    handle.addEventListener('pointerup', release, options);
    handle.addEventListener('pointercancel', release, options);
    handle.addEventListener('lostpointercapture', release, options);
    handle.addEventListener('keydown', event => {
        if (!active()) return;
        const value = { ArrowLeft: share - 2, ArrowRight: share + 2, Home: 25, End: 75 }[event.key];
        if (value === undefined) return;
        event.preventDefault();
        update(value);
    }, options);
    update(share);
    splitters.set(dialog, () => {
        if (pointer !== null && handle.hasPointerCapture(pointer)) handle.releasePointerCapture(pointer);
        controller.abort();
    });
}

export function dispose(dialog) {
    if (!dialog) return;
    finish(dialog, false, false);
    splitters.get(dialog)?.();
    splitters.delete(dialog);
}
