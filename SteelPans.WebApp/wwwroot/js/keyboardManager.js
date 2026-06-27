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

    function consumeEvent(event) {
        if (event.cancelable) {
            event.preventDefault();
        }

        event.stopPropagation();
        event.stopImmediatePropagation();
    }

    function isEditableTarget(target) {
        const tagName = target?.tagName?.toLowerCase() ?? null;

        return (
            tagName === "input" ||
            tagName === "textarea" ||
            tagName === "select" ||
            target?.isContentEditable === true
        );
    }

    async function onKeyDown(event) {
        if (!dotNetRef) {
            return;
        }

        const target = event.target;
        const tagName = target?.tagName?.toLowerCase() ?? null;
        const editableTarget = isEditableTarget(target);

        const modalOpen = document.querySelector(".modal-popup") !== null;

        const shouldConsumeSynchronously =
            modalOpen &&
            !editableTarget &&
            (event.key === "Enter" || event.key === "Escape");

        if (shouldConsumeSynchronously) {
            consumeEvent(event);
        }

        const consume = await dotNetRef.invokeMethodAsync("OnKeyDownAsync", {
            key: event.key,
            code: event.code,
            ctrlKey: event.ctrlKey,
            shiftKey: event.shiftKey,
            altKey: event.altKey,
            metaKey: event.metaKey,
            repeat: event.repeat,
            targetTagName: tagName,
            isEditableTarget: editableTarget
        });

        if (consume === true && !shouldConsumeSynchronously) {
            consumeEvent(event);
        }
    }

    return {
        initialize,
        dispose
    };
})();