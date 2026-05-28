window.keyboardManager = (() => {
    let dotNetRef = null;

    function initialize(ref) {
        dotNetRef = ref;

        document.removeEventListener("keydown", onKeyDown, true);
        document.addEventListener("keydown", onKeyDown, true);
    }

    function dispose() {
        document.removeEventListener("keydown", onKeyDown, true);
        dotNetRef = null;
    }

    function onKeyDown(event) {
        if (!dotNetRef) {
            return;
        }

        const target = event.target;
        const tagName = target?.tagName?.toLowerCase() ?? null;

        const isEditableTarget =
            tagName === "input" ||
            tagName === "textarea" ||
            tagName === "select" ||
            target?.isContentEditable === true;

        dotNetRef.invokeMethodAsync("OnKeyDownAsync", {
            key: event.key,
            code: event.code,
            ctrlKey: event.ctrlKey,
            shiftKey: event.shiftKey,
            altKey: event.altKey,
            metaKey: event.metaKey,
            repeat: event.repeat,
            targetTagName: tagName,
            isEditableTarget
        });
    }

    return {
        initialize,
        dispose
    };
})();