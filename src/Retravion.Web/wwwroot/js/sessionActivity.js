// Only real user input reports activity; there is deliberately no heartbeat.
// Wheel, keyboard, pointer and touch input cover scrolling without counting
// programmatic scroll/focus changes caused by rendering as user presence.
const events = ["pointerdown", "pointermove", "click", "keydown", "input", "change", "wheel", "touchstart", "touchmove"];
let stopCurrent;

export function start(receiver) {
    stop();
    let stopped = false;
    let sending = false;
    let pending = false;
    const report = async event => {
        if (!event.isTrusted || stopped) return;
        pending = true;
        if (sending) return;
        sending = true;
        try {
            // Coalesce input while an interop call is in flight, without a polling timer.
            while (pending && !stopped) {
                pending = false;
                const active = await receiver.invokeMethodAsync("RecordActivity");
                if (!stopped && !active) {
                    stop();
                    window.location.assign("/");
                }
            }
        } catch {
            // A disconnected circuit cannot record activity. A later real input may
            // retry after reconnection; the server never revives an expired session.
        } finally {
            sending = false;
        }
    };
    for (const name of events) document.addEventListener(name, report, { capture: true, passive: true });
    stopCurrent = () => {
        stopped = true;
        for (const name of events) document.removeEventListener(name, report, true);
    };
}

export function stop() {
    stopCurrent?.();
    stopCurrent = undefined;
}
