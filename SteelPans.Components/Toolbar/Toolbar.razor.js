
export function observeOutsidePointerDown(toolbar, dotNetRef) {
    const onPointerDown = event => {
        if (!toolbar.isConnected)
            return;

        if (toolbar.dataset.open !== "true")
            return;

        if (event.composedPath().includes(toolbar))
            return;

        dotNetRef.invokeMethodAsync("OnOutsidePointerDownAsync")
            .catch(() => { });
    };

    document.addEventListener("pointerdown", onPointerDown, true);

    return {
        dispose() {
            document.removeEventListener("pointerdown", onPointerDown, true);
        }
    };
}
