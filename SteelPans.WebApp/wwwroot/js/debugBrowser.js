async function heartbeat() {
    try {
        await fetch("/api/dev/browser-heartbeat", {
            method: "POST",
            cache: "no-store"
        });
    } catch {
        return;
    }

    setTimeout(heartbeat, 250);
}

heartbeat();