window.mobileDrawer = {
    _drawers: new WeakMap(),

    initialize(
        element,
        side,
        initiallyOpen,
        dotNetRef,
        responsiveSide = null,
        responsiveThreshold = null) {

        const lip =
            element.querySelector(".mobile-drawer__lip");

        const content =
            element.querySelector(".mobile-drawer__content");

        const state = {
            defaultSide: side,
            side,
            responsiveSide,
            responsiveThreshold,

            lip,
            content,
            dotNetRef,

            open: initiallyOpen,
            peekOpen: false,

            pointerId: null,
            startPointer: 0,
            startOffset: 0,
            offset: 0,

            size: 0,
            hasMeasured: false,

            lastTapTime: 0,

            crossSize: null,
            animatingCrossSize: false,
            resizeObserver: null,
        };

        this._drawers.set(
            element,
            state);

        this._updateSide(
            element,
            state);

        state.onPointerDown = event => {
            if (!event.isPrimary)
                return;

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
            if (event.pointerId !==
                state.pointerId) {
                return;
            }

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
            if (event.pointerId !==
                state.pointerId) {
                return;
            }

            const pointerPosition =
                this._getPointerPosition(
                    event,
                    state.side);

            const pointerDistance =
                Math.abs(
                    pointerPosition -
                    state.startPointer);

            state.pointerId =
                null;

            element.style.transitionProperty =
                "";

            if (pointerDistance <= 10) {
                const now =
                    performance.now();

                /*
                 * Second tap while closed/peeked:
                 * open fully.
                 */
                if (!state.open &&
                    now - state.lastTapTime <= 300) {

                    state.lastTapTime =
                        0;

                    this._setOpen(
                        element,
                        state,
                        true,
                        true);

                    return;
                }

                state.lastTapTime =
                    now;

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
                state.lastTapTime =
                    0;
            }

            this._finishDrag(
                element,
                state);
        };

        state.onDocumentPointerDown = event => {
            if (!state.open &&
                !state.peekOpen) {
                return;
            }

            if (element.contains(event.target))
                return;

            /*
             * Peek isn't considered logically open,
             * so collapse it without notifying .NET.
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

        state.onResize = () => {
            this._syncLayout(
                element,
                state);
        };

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

        state.resizeObserver =
            new ResizeObserver(() => {
                if (state.animatingCrossSize)
                    return;

                /*
                 * Content can change both:
                 *
                 * - the drawer's slide distance
                 * - its max-content width/height
                 *
                 * Update the slide distance immediately,
                 * then animate the cross-axis size.
                 */
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
            state.resizeObserver.observe(
                content);
        }

        this._syncLayout(
            element,
            state);

        const rect =
            element.getBoundingClientRect();

        if (rect.width > 0 &&
            rect.height > 0) {

            state.crossSize =
                this._getCrossSize(
                    rect,
                    state.side);
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

        state.element.removeEventListener(
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

        this._drawers.delete(
            element);
    },

    _syncLayout(element, state) {
        element.classList.add(
            "mobile-drawer--no-transition");

        const previousSide =
            state.side;

        this._updateSide(
            element,
            state);

        if (previousSide !==
            state.side) {

            state.crossSize =
                null;
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

        element.classList.remove(
            "mobile-drawer--initially-open",
            "mobile-drawer--initially-closed");

        state.hasMeasured =
            true;

        const rect =
            element.getBoundingClientRect();

        if (state.crossSize === null) {
            state.crossSize =
                this._getCrossSize(
                    rect,
                    state.side);
        }

        requestAnimationFrame(() => {
            if (this._drawers.get(element) !==
                state) {
                return;
            }

            element.classList.remove(
                "mobile-drawer--no-transition");
        });
    },

    _updateSide(element, state) {
        let newSide =
            state.defaultSide;

        if (state.responsiveSide &&
            state.responsiveThreshold !== null &&
            window.innerWidth <
            state.responsiveThreshold) {

            newSide =
                state.responsiveSide;
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

        state.side =
            newSide;

        element.classList.add(
            `mobile-drawer--${newSide}`);
    },

    _animateCrossSize(element, state) {
        if (state.animatingCrossSize)
            return;

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
         * Let max-content calculate its new
         * natural size.
         */
        element.style[property] =
            "";

        const naturalRect =
            element.getBoundingClientRect();

        const newSize =
            this._getCrossSize(
                naturalRect,
                state.side);

        if (previousSize === null) {
            state.crossSize =
                newSize;

            this._syncLayout(
                element,
                state);

            return;
        }

        if (Math.abs(
            previousSize -
            newSize) < 0.5) {

            state.crossSize =
                newSize;

            this._syncLayout(
                element,
                state);

            return;
        }

        state.animatingCrossSize =
            true;

        /*
         * Lock at the old size.
         */
        element.style[property] =
            `${previousSize}px`;

        element.getBoundingClientRect();

        /*
         * Calculate the final clamped position
         * using the final drawer size.
         */
        const previousTransition =
            element.style.transition;

        element.style.transition =
            "none";

        element.style[property] =
            `${newSize}px`;

        this._updatePosition(
            element,
            state);

        element.getBoundingClientRect();

        /*
         * Restore starting size.
         */
        element.style[property] =
            `${previousSize}px`;

        element.getBoundingClientRect();

        element.style.transition =
            previousTransition;

        requestAnimationFrame(() => {
            if (this._drawers.get(element) !==
                state) {
                return;
            }

            element.style[property] =
                `${newSize}px`;
        });

        const onTransitionEnd = event => {
            if (event.target !== element ||
                event.propertyName !== property) {
                return;
            }

            element.removeEventListener(
                "transitionend",
                onTransitionEnd);

            state.crossSize =
                newSize;

            state.animatingCrossSize =
                false;

            /*
             * Return sizing to max-content.
             */
            element.style[property] =
                "";

            this._syncLayout(
                element,
                state);
        };

        element.addEventListener(
            "transitionend",
            onTransitionEnd);
    },

    _updatePosition(element, state) {
        /*
         * Clear the previous calculated position so
         * --drawer-position is resolved again.
         */
        element.style.left =
            "";

        element.style.top =
            "";

        const style =
            getComputedStyle(element);

        const rect =
            element.getBoundingClientRect();

        const pagePaddingValue =
            parseFloat(
                style.getPropertyValue(
                    "--page-padding"));

        const pagePadding =
            Number.isFinite(
                pagePaddingValue)
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
                    Number.isFinite(
                        requestedPosition)
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
                    Number.isFinite(
                        requestedPosition)
                        ? requestedPosition
                        : minPosition,
                    minPosition,
                    maxPosition);

            element.style.top =
                `${position}px`;
        }
    },

    _setPeekOpen(element, state) {
        if (!state.hasMeasured)
            return;

        const peekSize =
            this._getPeekSize(
                element,
                state);

        state.peekOpen =
            true;

        state.offset =
            peekSize;

        element.style.transitionProperty =
            "";

        this._applyOffset(
            element,
            state);
    },

    _setPeekClosed(element, state) {
        state.peekOpen =
            false;

        state.offset =
            0;

        element.style.transitionProperty =
            "";

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

    _syncOffsetForState(element, state) {
        if (state.open) {
            state.offset =
                state.size;

            return;
        }

        if (state.peekOpen) {
            state.offset =
                this._getPeekSize(
                    element,
                    state);

            return;
        }

        state.offset =
            0;
    },

    _finishDrag(element, state) {
        const open =
            state.offset >=
            state.size * 0.5;

        state.peekOpen =
            false;

        this._setOpen(
            element,
            state,
            open,
            true);
    },

    _setOpen(
        element,
        state,
        open,
        notifyDotNet) {

        const changed =
            state.open !== open;

        state.open =
            open;

        element.classList.add(open ? "mobile-drawer--open" : "mobile-drawer--closed");
        element.classList.remove(open ? "mobile-drawer--closed" : "mobile-drawer--open");

        state.peekOpen =
            false;

        if (!state.hasMeasured) {
            element.classList.toggle(
                "mobile-drawer--initially-open",
                open);

            element.classList.toggle(
                "mobile-drawer--initially-closed",
                !open);
        }
        else {
            state.offset =
                open
                    ? state.size
                    : 0;

            element.style.transitionProperty =
                "";

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
                    rect.height -
                    lipSize);
        }
        else {
            state.size =
                Math.max(
                    0,
                    rect.width -
                    lipSize);
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