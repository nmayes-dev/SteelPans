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
            this._syncLayout(element, state);
        };

        state.resizeObserver = new ResizeObserver(() => {
            this._syncLayout(element, state);
        });

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

        state.resizeObserver.observe(element);

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

        state.resizeObserver?.disconnect();

        this._drawers.delete(element);
    },

    _syncLayout(element, state) {
        element.classList.add(
            "mobile-drawer--no-transition");

        this._updateSide(element, state);

        if (!this._updateSize(element, state))
            return;

        this._updatePosition(element, state);

        state.offset = state.open
            ? state.size
            : 0;

        this._applyOffset(element, state);

        element.classList.remove(
            "mobile-drawer--initially-open",
            "mobile-drawer--initially-closed");

        state.hasMeasured = true;

        requestAnimationFrame(() => {
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

    _updatePosition(element, state) {
        /*
         * Remove the previously calculated inline position so the original
         * --drawer-position value is resolved again after resizing.
         */
        element.style.left = "";
        element.style.top = "";

        const style =
            getComputedStyle(element);

        const rect =
            element.getBoundingClientRect();

        const sidePadding = parseFloat(
            style.getPropertyValue("--page-padding"));

        const padding =
            Number.isFinite(sidePadding)
                ? sidePadding
                : 0;

        if (state.side === "top" ||
            state.side === "bottom") {

            const requestedPosition =
                parseFloat(style.left);

            const viewportSize =
                document.documentElement.clientWidth;

            const minPosition =
                padding;

            const maxPosition =
                Math.max(
                    minPosition,
                    viewportSize - rect.width - padding);

            const position =
                this._clamp(
                    Number.isFinite(requestedPosition)
                        ? requestedPosition
                        : minPosition,
                    minPosition,
                    maxPosition);

            element.style.left =
                `${position}px`;
        }
        else {
            const requestedPosition =
                parseFloat(style.top);

            const viewportSize =
                document.documentElement.clientHeight;

            const minPosition =
                padding;

            const maxPosition =
                Math.max(
                    minPosition,
                    viewportSize - rect.height - padding);

            const position =
                this._clamp(
                    Number.isFinite(requestedPosition)
                        ? requestedPosition
                        : minPosition,
                    minPosition,
                    maxPosition);

            element.style.top =
                `${position}px`;
        }
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

        if (rect.width === 0 ||
            rect.height === 0) {
            return false;
        }

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
                    `translate3d(0, ${hidden}px, 0)`;
                break;

            case "top":
                element.style.transform =
                    `translate3d(0, ${-hidden}px, 0)`;
                break;

            case "left":
                element.style.transform =
                    `translate3d(${-hidden}px, 0, 0)`;
                break;

            case "right":
                element.style.transform =
                    `translate3d(${hidden}px, 0, 0)`;
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