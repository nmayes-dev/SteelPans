const mobileDrawer = {
    _drawers: new WeakMap(),

    initialize(
        element,
        side,
        initiallyOpen,
        dotNetRef,
        responsiveSide = null,
        responsiveThreshold = null) {

        if (this._drawers.has(element))
            this.dispose(element);

        const content =
            element.querySelector(".mobile-drawer__content");

        const state = {
            defaultSide: side,
            side,
            responsiveSide,
            responsiveThreshold,

            content,
            dotNetRef,

            open: false,
            peekOpen: false,
            initialOpenPending: initiallyOpen,

            pointerId: null,
            startPointer: 0,
            startOffset: 0,
            offset: 0,

            size: 0,
            hasMeasured: false,
            ready: false,

            lastTapTime: 0,

            crossSize: null,
            animatingCrossSize: false,
            resizeObserver: null,

            startupFrame1: null,
            startupFrame2: null,
            layoutFrame: null,
            crossSizeFrame: null,
            crossSizeHandler: null,
        };

        this._drawers.set(element, state);

        /* Pointer handling */

        state.onPointerDown = event => {
            if (!event.isPrimary ||
                !state.hasMeasured) {
                return;
            }

            this._cancelInitialOpen(
                element,
                state);

            state.pointerId =
                event.pointerId;

            state.startPointer =
                this._getPointerPosition(
                    event,
                    state.side);

            state.startOffset =
                state.offset;

            element.style.transitionProperty =
                "none";

            event.preventDefault();
        };

        state.onPointerMove = event => {
            if (event.pointerId !== state.pointerId)
                return;

            const current =
                this._getPointerPosition(
                    event,
                    state.side);

            const delta =
                current -
                state.startPointer;

            state.offset =
                this._clamp(
                    state.startOffset +
                    this._normalizeDelta(
                        delta,
                        state.side),
                    0,
                    state.size);

            this._applyOffset(
                element,
                state);
        };

        state.onPointerUp = event => {
            if (event.pointerId !== state.pointerId)
                return;

            const pointerPosition =
                this._getPointerPosition(
                    event,
                    state.side);

            const pointerDistance =
                Math.abs(
                    pointerPosition -
                    state.startPointer);

            state.pointerId = null;

            element.style.transitionProperty = "";

            if (pointerDistance <= 10) {
                const now = performance.now();

                /*
                 * Second tap while closed or peeked:
                 * open fully.
                 */
                if (!state.open &&
                    now - state.lastTapTime <= 300) {

                    state.lastTapTime = 0;

                    this._setOpen(
                        element,
                        state,
                        true,
                        true);

                    return;
                }

                state.lastTapTime = now;

                /*
                 * First tap while closed:
                 * reveal a small amount.
                 */
                if (!state.open) {
                    this._setPeekOpen(
                        element,
                        state);

                    return;
                }
            }
            else {
                state.lastTapTime = 0;
            }

            this._finishDrag(
                element,
                state);
        };

        /* Outside pointer handling */

        state.onDocumentPointerDown = event => {
            if (!state.open &&
                !state.peekOpen) {
                return;
            }

            if (element.contains(event.target))
                return;

            /*
             * Peek is not logically open.
             * Collapse without notifying .NET.
             */
            if (state.peekOpen) {
                this._setPeekClosed(
                    element,
                    state);

                return;
            }

            this._setOpen(
                element,
                state,
                false,
                true);
        };

        /* Window resize */

        state.onResize = () => {
            this._cancelCrossSizeAnimation(
                element,
                state);

            this._syncLayout(
                element,
                state);
        };

        /* Register listeners */

        element.addEventListener(
            "pointerdown",
            state.onPointerDown);

        window.addEventListener(
            "pointermove",
            state.onPointerMove);

        window.addEventListener(
            "pointerup",
            state.onPointerUp);

        window.addEventListener(
            "pointercancel",
            state.onPointerUp);

        document.addEventListener(
            "pointerdown",
            state.onDocumentPointerDown);

        window.addEventListener(
            "resize",
            state.onResize);

        /*
         * Establish the closed layout before
         * observing content changes.
         */
        this._syncLayout(
            element,
            state);

        /* Content resizing */

        state.resizeObserver =
            new ResizeObserver(() => {
                if (state.animatingCrossSize)
                    return;

                /*
                 * Until initialization has finished,
                 * update geometry without animation.
                 */
                if (!state.hasMeasured ||
                    !state.ready) {

                    this._syncLayout(
                        element,
                        state);

                    return;
                }

                if (this._updateSize(
                    element,
                    state)) {

                    this._syncOffsetForState(
                        element,
                        state);

                    this._applyOffset(
                        element,
                        state);
                }

                this._animateCrossSize(
                    element,
                    state);
            });

        if (content) {
            state.resizeObserver.observe(content);
        }
    },

    close(element) {
        const state =
            this._drawers.get(element);

        if (!state)
            return;

        this._setOpen(
            element,
            state,
            false,
            false);
    },

    dispose(element) {
        const state =
            this._drawers.get(element);

        if (!state)
            return;

        this._drawers.delete(element);

        this._cancelFrames(state);

        if (state.crossSizeHandler) {
            element.removeEventListener(
                "transitionend",
                state.crossSizeHandler);
        }

        element.removeEventListener(
            "pointerdown",
            state.onPointerDown);

        window.removeEventListener(
            "pointermove",
            state.onPointerMove);

        window.removeEventListener(
            "pointerup",
            state.onPointerUp);

        window.removeEventListener(
            "pointercancel",
            state.onPointerUp);

        document.removeEventListener(
            "pointerdown",
            state.onDocumentPointerDown);

        window.removeEventListener(
            "resize",
            state.onResize);

        state.resizeObserver?.disconnect();
    },

    /* Initialization */

    _scheduleInitialOpen(element, state) {
        /*
         * The drawer is currently:
         *
         * - measured
         * - positioned
         * - closed
         * - visible
         * - not transitioning
         *
         * Wait for the browser to paint that
         * state before starting the animation.
         */

        state.startupFrame1 =
            requestAnimationFrame(() => {
                state.startupFrame1 = null;

                if (this._drawers.get(element) !== state ||
                    !state.initialOpenPending) {
                    return;
                }

                state.startupFrame2 =
                    requestAnimationFrame(() => {
                        state.startupFrame2 = null;

                        if (this._drawers.get(element) !== state ||
                            !state.initialOpenPending) {
                            return;
                        }

                        state.initialOpenPending = false;
                        state.ready = true;

                        /*
                         * Enable transitions while the
                         * drawer is still closed.
                         */
                        element.classList.remove(
                            "mobile-drawer--no-transition");

                        /*
                         * Commit the closed style before
                         * changing the transform.
                         */
                        element.getBoundingClientRect();

                        /*
                         * Animate open and notify .NET.
                         */
                        this._setOpen(
                            element,
                            state,
                            true,
                            true);
                    });
            });
    },

    _cancelInitialOpen(element, state) {
        if (!state.initialOpenPending)
            return;

        state.initialOpenPending = false;

        if (state.startupFrame1 !== null) {
            cancelAnimationFrame(
                state.startupFrame1);

            state.startupFrame1 = null;
        }

        if (state.startupFrame2 !== null) {
            cancelAnimationFrame(
                state.startupFrame2);

            state.startupFrame2 = null;
        }

        if (state.hasMeasured) {
            this._scheduleTransitionRestore(
                element,
                state);
        }
    },

    _scheduleTransitionRestore(element, state) {
        if (state.layoutFrame !== null) {
            cancelAnimationFrame(
                state.layoutFrame);
        }

        state.layoutFrame =
            requestAnimationFrame(() => {
                state.layoutFrame = null;

                if (this._drawers.get(element) !== state)
                    return;

                element.classList.remove(
                    "mobile-drawer--no-transition");

                state.ready = true;
            });
    },

    _cancelFrames(state) {
        for (const frame of [
            state.startupFrame1,
            state.startupFrame2,
            state.layoutFrame,
            state.crossSizeFrame
        ]) {
            if (frame !== null)
                cancelAnimationFrame(frame);
        }

        state.startupFrame1 = null;
        state.startupFrame2 = null;
        state.layoutFrame = null;
        state.crossSizeFrame = null;
    },

    /* Layout */

    _syncLayout(element, state) {
        /*
         * Disable transitions while recalculating
         * the drawer's dimensions and position.
         */
        element.classList.add(
            "mobile-drawer--no-transition");

        state.ready = false;

        const previousSide = state.side;

        this._updateSide(
            element,
            state);

        if (previousSide !== state.side) {
            state.crossSize = null;

            /*
             * Clear any dimensions left over from
             * cross-axis size animations.
             */
            element.style.width = "";
            element.style.height = "";
        }

        if (!this._updateSize(
            element,
            state)) {
            return;
        }

        this._updatePosition(
            element,
            state);

        this._syncOffsetForState(
            element,
            state);

        this._applyOffset(
            element,
            state);

        const firstLayout =
            !state.hasMeasured;

        state.hasMeasured = true;

        const rect =
            element.getBoundingClientRect();

        if (state.crossSize === null) {
            state.crossSize =
                this._getCrossSize(
                    rect,
                    state.side);
        }

        if (firstLayout) {
            /*
             * The closed transform and clamped
             * position are now both correct.
             */
            element.classList.remove(
                "mobile-drawer--pending");

            if (state.initialOpenPending) {
                this._scheduleInitialOpen(
                    element,
                    state);
            }
            else {
                this._scheduleTransitionRestore(
                    element,
                    state);
            }

            return;
        }

        /*
         * During initial opening, leave
         * transitions disabled until the
         * scheduled opening frame.
         */
        if (state.initialOpenPending)
            return;

        this._scheduleTransitionRestore(
            element,
            state);
    },

    _updateSide(element, state) {
        let newSide = state.defaultSide;

        if (state.responsiveSide &&
            state.responsiveThreshold !== null &&
            window.innerWidth <
            state.responsiveThreshold) {

            newSide = state.responsiveSide;
        }

        if (state.side === newSide &&
            element.classList.contains(
                `mobile-drawer--${newSide}`)) {
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
         * Clear previously calculated positions
         * so --drawer-position is resolved again.
         */
        element.style.left = "";
        element.style.top = "";

        const style =
            getComputedStyle(element);

        const rect =
            element.getBoundingClientRect();

        const pagePaddingValue =
            parseFloat(
                style.getPropertyValue(
                    "--page-padding"));

        const pagePadding =
            Number.isFinite(pagePaddingValue)
                ? pagePaddingValue
                : 0;

        if (this._isHorizontal(state.side)) {
            const requestedPosition =
                parseFloat(style.left);

            const viewportSize =
                document.documentElement.clientWidth;

            const minPosition =
                pagePadding;

            const maxPosition =
                Math.max(
                    minPosition,
                    viewportSize -
                    rect.width -
                    pagePadding);

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
                pagePadding;

            const maxPosition =
                Math.max(
                    minPosition,
                    viewportSize -
                    rect.height -
                    pagePadding);

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

    /* Cross-axis resizing */

    _animateCrossSize(element, state) {
        if (state.animatingCrossSize ||
            !state.ready) {
            return;
        }

        const currentRect =
            element.getBoundingClientRect();

        if (currentRect.width === 0 ||
            currentRect.height === 0) {
            return;
        }

        const property =
            this._isHorizontal(state.side)
                ? "width"
                : "height";

        const previousSize =
            state.crossSize;

        /*
         * Let max-content determine the natural
         * size after content has changed.
         */
        element.style[property] = "";

        const naturalRect =
            element.getBoundingClientRect();

        const newSize =
            this._getCrossSize(
                naturalRect,
                state.side);

        if (previousSize === null) {
            state.crossSize = newSize;

            this._updatePosition(
                element,
                state);

            return;
        }

        if (Math.abs(previousSize - newSize) < 0.5) {
            state.crossSize = newSize;

            this._updatePosition(
                element,
                state);

            return;
        }

        state.animatingCrossSize = true;

        /*
         * Start from the previously measured size.
         */
        element.style[property] =
            `${previousSize}px`;

        element.getBoundingClientRect();

        /*
         * Calculate the final clamped position
         * using the target size.
         */
        const previousTransition =
            element.style.transition;

        element.style.transition = "none";

        element.style[property] =
            `${newSize}px`;

        this._updatePosition(
            element,
            state);

        element.getBoundingClientRect();

        /*
         * Restore the original size before
         * enabling transitions again.
         */
        element.style[property] =
            `${previousSize}px`;

        element.getBoundingClientRect();

        element.style.transition =
            previousTransition;

        state.crossSizeHandler = event => {
            if (event.target !== element ||
                event.propertyName !== property) {
                return;
            }

            element.removeEventListener(
                "transitionend",
                state.crossSizeHandler);

            state.crossSizeHandler = null;

            state.crossSize = newSize;
            state.animatingCrossSize = false;

            /*
             * Return to natural sizing.
             */
            element.style[property] = "";

            this._syncLayout(
                element,
                state);
        };

        element.addEventListener(
            "transitionend",
            state.crossSizeHandler);

        state.crossSizeFrame =
            requestAnimationFrame(() => {
                state.crossSizeFrame = null;

                if (this._drawers.get(element) !== state)
                    return;

                element.style[property] =
                    `${newSize}px`;
            });
    },

    _cancelCrossSizeAnimation(element, state) {
        if (!state.animatingCrossSize)
            return;

        if (state.crossSizeFrame !== null) {
            cancelAnimationFrame(
                state.crossSizeFrame);

            state.crossSizeFrame = null;
        }

        if (state.crossSizeHandler) {
            element.removeEventListener(
                "transitionend",
                state.crossSizeHandler);

            state.crossSizeHandler = null;
        }

        state.animatingCrossSize = false;
        state.crossSize = null;

        element.style.width = "";
        element.style.height = "";
    },

    /* Open / closed state */

    _setOpen(
        element,
        state,
        open,
        notifyDotNet) {

        this._cancelInitialOpen(
            element,
            state);

        const changed =
            state.open !== open;

        state.open = open;
        state.peekOpen = false;

        element.classList.toggle(
            "mobile-drawer--open",
            open);

        element.classList.toggle(
            "mobile-drawer--closed",
            !open);

        if (state.hasMeasured) {
            state.offset =
                open
                    ? state.size
                    : 0;

            element.style.transitionProperty = "";

            this._applyOffset(
                element,
                state);
        }

        if (!changed ||
            !notifyDotNet) {
            return;
        }

        const method =
            open
                ? "NotifyOpenedFromJsAsync"
                : "NotifyClosedFromJsAsync";

        state.dotNetRef
            .invokeMethodAsync(method)
            .catch(() => {
            });
    },

    _syncOffsetForState(element, state) {
        if (state.open) {
            state.offset = state.size;
            return;
        }

        if (state.peekOpen) {
            state.offset =
                this._getPeekSize(
                    element,
                    state);

            return;
        }

        state.offset = 0;
    },

    _finishDrag(element, state) {
        const open =
            state.offset >=
            state.size * 0.5;

        state.peekOpen = false;

        this._setOpen(
            element,
            state,
            open,
            true);
    },

    /* Peek state */

    _setPeekOpen(element, state) {
        if (!state.hasMeasured)
            return;

        state.peekOpen = true;

        state.offset =
            this._getPeekSize(
                element,
                state);

        element.style.transitionProperty = "";

        this._applyOffset(
            element,
            state);
    },

    _setPeekClosed(element, state) {
        state.peekOpen = false;
        state.offset = 0;

        element.style.transitionProperty = "";

        this._applyOffset(
            element,
            state);
    },

    _getPeekSize(element, state) {
        const value =
            parseFloat(
                getComputedStyle(element)
                    .getPropertyValue(
                        "--tap-open-size"));

        const peekSize =
            Number.isFinite(value)
                ? value
                : 48;

        return this._clamp(
            peekSize,
            0,
            state.size);
    },

    /* Size and transform */

    _updateSize(element, state) {
        const rect =
            element.getBoundingClientRect();

        if (rect.width === 0 ||
            rect.height === 0) {
            return false;
        }

        const lipSize =
            parseFloat(
                getComputedStyle(element)
                    .getPropertyValue(
                        "--lip-size"));

        if (!Number.isFinite(lipSize))
            return false;

        if (this._isHorizontal(state.side)) {
            state.size =
                Math.max(
                    0,
                    rect.height - lipSize);
        }
        else {
            state.size =
                Math.max(
                    0,
                    rect.width - lipSize);
        }

        return true;
    },

    _applyOffset(element, state) {
        const hidden =
            state.size -
            state.offset;

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

    /* Helpers */

    _getPointerPosition(event, side) {
        return this._isHorizontal(side)
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

    _getCrossSize(rect, side) {
        return this._isHorizontal(side)
            ? rect.width
            : rect.height;
    },

    _isHorizontal(side) {
        return side === "top" ||
            side === "bottom";
    },

    _clamp(value, min, max) {
        return Math.min(
            max,
            Math.max(
                min,
                value));
    }
};

// Public ES module API. Keep method calls bound to the controller object,
// since its methods use `this` to access shared helpers and drawer state.
export function initialize(element, side, initiallyOpen, dotNetRef, responsiveSide = null, responsiveThreshold = null) {
    mobileDrawer.initialize(element, side, initiallyOpen, dotNetRef, responsiveSide, responsiveThreshold);
}

export function close(element) {
    mobileDrawer.close(element);
}

export function dispose(element) {
    mobileDrawer.dispose(element);
}
