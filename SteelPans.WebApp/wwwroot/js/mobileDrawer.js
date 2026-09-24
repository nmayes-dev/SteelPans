window.mobileDrawer = {
    _drawers: new WeakMap(),

    initialize(
        element,
        side,
        initiallyOpen,
        dotNetRef,
        responsiveSide = null,
        responsiveBreakpoint = null) {

        const lip = element.querySelector(".mobile-drawer__lip");

        const state = {
            defaultSide: side,
            side,
            responsiveSide,
            responsiveBreakpoint,

            lip,
            dotNetRef,
            open: initiallyOpen,

            pointerId: null,
            startPointer: 0,
            startOffset: 0,
            offset: 0,

            size: 0,
            hasMeasured: false,

            lastTapTime: 0,
        };

        this._drawers.set(element, state);

        /*
         * Do this immediately even if the component is currently hidden.
         * The initial CSS classes can then use the correct responsive edge
         * before the drawer becomes visible.
         */
        this._updateSide(element, state);

        state.onPointerDown = event => {
            if (!event.isPrimary)
                return;

            state.pointerId = event.pointerId;

            lip.setPointerCapture(event.pointerId);

            state.startPointer =
                this._getPointerPosition(event, state.side);

            state.startOffset = state.offset;

            element.style.transition = "none";

            event.preventDefault();
        };

        state.onPointerMove = event => {
            if (event.pointerId !== state.pointerId)
                return;

            const current =
                this._getPointerPosition(event, state.side);

            const delta =
                current - state.startPointer;

            state.offset = this._clamp(
                state.startOffset +
                this._normalizeDelta(delta, state.side),
                0,
                state.size);

            this._applyOffset(element, state);

            event.preventDefault();
        };

        state.onPointerUp = event => {
            if (event.pointerId !== state.pointerId)
                return;

            const pointerPosition =
                this._getPointerPosition(event, state.side);

            const pointerDistance =
                Math.abs(pointerPosition - state.startPointer);

            state.pointerId = null;

            /*
             * Treat a pointer interaction as a tap only if it hasn't moved
             * far enough to reasonably be a drag.
             */
            if (pointerDistance <= 10) {
                const now = performance.now();

                if (now - state.lastTapTime <= 300) {
                    state.lastTapTime = 0;

                    if (!state.open) {
                        this._setOpen(
                            element,
                            state,
                            true,
                            true);

                        return;
                    }
                }
                else {
                    state.lastTapTime = now;
                }
            }
            else {
                state.lastTapTime = 0;
            }

            this._finishDrag(element, state);
        };

        state.onDocumentPointerDown = event => {
            if (!state.open)
                return;

            if (element.contains(event.target))
                return;

            this._setOpen(
                element,
                state,
                false,
                true);
        };

        state.onResize = () => {
            /*
             * Resizing can change the responsive edge or make a previously
             * display:none drawer visible. Synchronise the layout without
             * animating between those layout states.
             */
            this._syncLayout(element, state);
        };

        lip.addEventListener(
            "pointerdown",
            state.onPointerDown);

        lip.addEventListener(
            "pointermove",
            state.onPointerMove);

        lip.addEventListener(
            "pointerup",
            state.onPointerUp);

        lip.addEventListener(
            "pointercancel",
            state.onPointerUp);

        document.addEventListener(
            "pointerdown",
            state.onDocumentPointerDown);

        window.addEventListener(
            "resize",
            state.onResize);

        this._syncLayout(element, state);
    },

    close(element) {
        const state = this._drawers.get(element);

        if (!state)
            return;

        this._setOpen(
            element,
            state,
            false,
            false);
    },

    dispose(element) {
        const state = this._drawers.get(element);

        if (!state)
            return;

        state.lip.removeEventListener(
            "pointerdown",
            state.onPointerDown);

        state.lip.removeEventListener(
            "pointermove",
            state.onPointerMove);

        state.lip.removeEventListener(
            "pointerup",
            state.onPointerUp);

        state.lip.removeEventListener(
            "pointercancel",
            state.onPointerUp);

        document.removeEventListener(
            "pointerdown",
            state.onDocumentPointerDown);

        window.removeEventListener(
            "resize",
            state.onResize);

        this._drawers.delete(element);
    },

    _syncLayout(element, state) {
        element.classList.add(
            "mobile-drawer--no-transition");

        this._updateSide(element, state);

        /*
         * If the component is currently hidden through display:none,
         * getBoundingClientRect() will be zero. In that case don't apply
         * an inline transform: leave the CSS initial state in control.
         */
        if (!this._updateSize(element, state))
            return;

        state.offset = state.open
            ? state.size
            : 0;

        this._applyOffset(element, state);

        /*
         * Once JS has a real measurement it can take over from the CSS-only
         * initial transform.
         */
        element.classList.remove(
            "mobile-drawer--initially-open",
            "mobile-drawer--initially-closed");

        state.hasMeasured = true;

        requestAnimationFrame(() => {
            /*
             * Ensure the current element/state hasn't been disposed before
             * enabling transitions again.
             */
            if (this._drawers.get(element) !== state)
                return;

            element.classList.remove(
                "mobile-drawer--no-transition");
        });
    },

    _updateSide(element, state) {
        let newSide = state.defaultSide;

        if (state.responsiveSide &&
            state.responsiveBreakpoint !== null &&
            window.innerWidth < state.responsiveBreakpoint) {

            newSide = state.responsiveSide;
        }

        if (state.side === newSide &&
            element.classList.contains(`mobile-drawer--${newSide}`)) {
            return;
        }

        element.classList.remove(
            "mobile-drawer--bottom",
            "mobile-drawer--top",
            "mobile-drawer--left",
            "mobile-drawer--right");

        state.side = newSide;

        element.classList.add(
            `mobile-drawer--${newSide}`);
    },

    _finishDrag(element, state) {
        const open =
            state.offset >= state.size * 0.5;

        this._setOpen(
            element,
            state,
            open,
            true);
    },

    _setOpen(element, state, open, notifyDotNet) {
        const changed =
            state.open !== open;

        state.open = open;

        /*
         * If it still hasn't been measurable, don't add an inline transform.
         * Change the CSS initial-state class instead.
         */
        if (!state.hasMeasured) {
            element.classList.toggle(
                "mobile-drawer--initially-open",
                open);

            element.classList.toggle(
                "mobile-drawer--initially-closed",
                !open);
        }
        else {
            state.offset = open
                ? state.size
                : 0;

            element.style.transition = "";

            this._applyOffset(element, state);
        }

        if (!changed || !notifyDotNet)
            return;

        const method = open
            ? "NotifyOpenedFromJsAsync"
            : "NotifyClosedFromJsAsync";

        state.dotNetRef
            .invokeMethodAsync(method)
            .catch(() => {
            });
    },

    _updateSize(element, state) {
        const rect =
            element.getBoundingClientRect();

        /*
         * display:none elements cannot be meaningfully measured.
         */
        if (rect.width === 0 || rect.height === 0)
            return false;

        const lipSize = parseFloat(
            getComputedStyle(element)
                .getPropertyValue("--lip-size"));

        if (!Number.isFinite(lipSize))
            return false;

        if (state.side === "top" ||
            state.side === "bottom") {

            state.size = Math.max(
                0,
                rect.height - lipSize);
        }
        else {
            state.size = Math.max(
                0,
                rect.width - lipSize);
        }

        return true;
    },

    _applyOffset(element, state) {
        const hidden =
            state.size - state.offset;

        switch (state.side) {
            case "bottom":
                element.style.transform =
                    `translate3d(-50%, ${hidden}px, 0)`;
                break;

            case "top":
                element.style.transform =
                    `translate3d(-50%, ${-hidden}px, 0)`;
                break;

            case "left":
                element.style.transform =
                    `translate3d(${-hidden}px, -50%, 0)`;
                break;

            case "right":
                element.style.transform =
                    `translate3d(${hidden}px, -50%, 0)`;
                break;
        }
    },

    _getPointerPosition(event, side) {
        return side === "top" ||
            side === "bottom"
            ? event.clientY
            : event.clientX;
    },

    _normalizeDelta(delta, side) {
        switch (side) {
            case "bottom":
            case "right":
                return -delta;

            case "top":
            case "left":
                return delta;

            default:
                return delta;
        }
    },

    _clamp(value, min, max) {
        return Math.min(
            max,
            Math.max(min, value));
    }
};