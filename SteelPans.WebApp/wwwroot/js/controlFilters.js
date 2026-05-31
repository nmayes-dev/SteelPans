window.filters = {
    numeric: function (element) {
        if (!element)
            return;

        const allowedKeys = new Set([
            "Backspace", "Delete",
            "ArrowLeft", "ArrowRight",
            "ArrowUp", "ArrowDown",
            "Tab", "Home", "End",
            "Enter", "Escape"
        ]);

        const allowedCtrlKeys = new Set([
            "a", "c", "v", "x", "z", "y"
        ]);

        element.addEventListener("keydown", (e) => {
            if (e.ctrlKey || e.metaKey) {
                const key = e.key.toLowerCase();

                if (allowedCtrlKeys.has(key))
                    return;
            }

            if (allowedKeys.has(e.key))
                return;

            if (/^[0-9]$/.test(e.key))
                return;

            e.preventDefault();
        });

        element.addEventListener("paste", (e) => {
            const text = (e.clipboardData || window.clipboardData).getData("text");

            if (!/^\d+$/.test(text))
                e.preventDefault();
        });
    },

    setValue: function (element, value) {
        if (!element)
            return;

        element.value = value ?? "";
    }
};