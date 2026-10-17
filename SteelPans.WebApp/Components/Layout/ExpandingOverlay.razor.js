const instances = new WeakMap();

export function initialize(root) {
    dispose(root);

    const surface = root.querySelector(".overlay__surface");

    if (!surface)
        return;

    const bottomMargin = 16;

    let disposed = false;
    let frame = 0;
    let lastTop = null;
    let lastBottom = null;

    function update() {
        if (disposed)
            return;

        const rect = surface.getBoundingClientRect();
        const viewport = window.visualViewport;

        const viewportBottom = viewport
            ? viewport.offsetTop + viewport.height
            : window.innerHeight;

        const top = Math.max(
            rect.top,
            viewport?.offsetTop ?? 0
        );

        if (top === lastTop && viewportBottom === lastBottom)
            return;

        lastTop = top;
        lastBottom = viewportBottom;

        const availableHeight = Math.max(
            0,
            viewportBottom - top - bottomMargin
        );

        root.style.setProperty(
            "--overlay__available-height",
            `${availableHeight}px`
        );
    }

    function scheduleUpdate() {
        if (disposed || frame)
            return;

        frame = requestAnimationFrame(() => {
            frame = 0;
            update();
        });
    }

    const resizeObserver = new ResizeObserver(scheduleUpdate);

    resizeObserver.observe(root);

    // Detect layout changes in containing elements.
    for (let parent = root.parentElement; parent; parent = parent.parentElement) {
        resizeObserver.observe(parent);
    }

    const mutationObserver = new MutationObserver(() => {
        lastTop = null;
        scheduleUpdate();
    });

    mutationObserver.observe(root, {
        attributes: true,
        attributeFilter: ["class"]
    });

    window.addEventListener("resize", scheduleUpdate);
    window.addEventListener("scroll", scheduleUpdate, true);

    window.visualViewport?.addEventListener("resize", scheduleUpdate);
    window.visualViewport?.addEventListener("scroll", scheduleUpdate);

    scheduleUpdate();

    instances.set(root, () => {
        disposed = true;

        cancelAnimationFrame(frame);

        resizeObserver.disconnect();
        mutationObserver.disconnect();

        window.removeEventListener("resize", scheduleUpdate);
        window.removeEventListener("scroll", scheduleUpdate, true);

        window.visualViewport?.removeEventListener("resize", scheduleUpdate);
        window.visualViewport?.removeEventListener("scroll", scheduleUpdate);

        root.style.removeProperty("--overlay__available-height");
    });
}

export function dispose(root) {
    const cleanup = instances.get(root);

    if (!cleanup)
        return;

    cleanup();
    instances.delete(root);
}