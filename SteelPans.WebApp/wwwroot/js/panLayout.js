const SELECTORS = {
    item: ".pans-page__assigned-pan",
    svg: ".sp-svg",
    copy: ".pans-page__assigned-pan-copy",
    leftGroup: ".pans-page__assigned-pan-copy-group--left",
    rightGroup: ".pans-page__assigned-pan-copy-group--right",
    label: ".pans-page__assigned-pan-copy-label",
    value: ".pans-page__assigned-pan-track, .pans-page__assigned-pan-type",
    panShape: ".sp-svg-circle, .sp-note, path, circle, ellipse, rect, polygon, polyline",
    noDrag: "[data-pan-layout-no-drag], button, a, input, select, textarea, label"
};

const DEFAULTS = {
    resizeSettleDelayMs: 40,
    dragStartThresholdPx: 6
};

window.panLayout = {
    _layoutCache: new Map(),
    _textMeasureCanvas: null,

    observe(container, dotNetRef = null) {
        if (!container) return;

        container.classList.add("pans-page__assigned-pans--loading");
        this.disconnect(container);

        container._panLayoutState = this.getState(container);
        container._panLayoutState.dotNetRef = dotNetRef;
        this.attachDragHandlers(container);

        container._panGridObserver = new ResizeObserver(() => {
            this.requestUpdateAfterResizeSettles(container);
        });

        container._panGridObserver.observe(container);

        container._panMutationObserver = new MutationObserver(() => {
            this.clearLayoutCaches(container);
            this.requestUpdate(container);
        });

        container._panMutationObserver.observe(container, {
            childList: true,
            subtree: true
        });

        this.requestUpdate(container, { startup: true });
    },

    disconnect(container) {
        if (!container) return;

        this.cancelPendingDrag(container);
        this.endDrag(container, { commit: false });
        this.detachDragHandlers(container);

        container._panGridObserver?.disconnect();
        container._panMutationObserver?.disconnect();

        const state = container._panLayoutState;

        if (state) {
            clearTimeout(state.resizeSettleTimer);
            cancelAnimationFrame(state.updateRaf);
            cancelAnimationFrame(state.textFitRaf);
        }

        delete container._panLayoutState;
    },

    requestUpdateAfterResizeSettles(container) {
        if (!container) return;

        const state = this.getState(container);
        const delay = this.numberFromDataset(
            container,
            "resizeSettleDelayMs",
            DEFAULTS.resizeSettleDelayMs
        );

        clearTimeout(state.resizeSettleTimer);

        state.resizeSettleTimer = setTimeout(() => {
            this.requestUpdate(container);
        }, delay);
    },

    requestUpdate(container, options = {}) {
        if (!container) return;

        const state = this.getState(container);
        const isStartupUpdate = options.startup === true && !state.startupComplete;
        state.startupInProgress = state.startupInProgress || isStartupUpdate;

        if (!state.startupInProgress) {
            cancelAnimationFrame(state.updateRaf);
            cancelAnimationFrame(state.textFitRaf);
        }

        state.updateRaf = requestAnimationFrame(() => {
            this.update(container);

            /*
               Layout must be committed before measurement.
               A second RAF guarantees DOM geometry is updated.
            */
            state.textFitRaf = requestAnimationFrame(() => {
                this.fitAllPanText(container);

                if (isStartupUpdate) {
                    this.completeStartupLayout(container);
                }
            });
        });
    },

    completeStartupLayout(container) {
        const state = this.getState(container);

        if (state.startupComplete) {
            return;
        }

        state.startupComplete = true;

        /*
           Wait one more frame so layout + text sizing have definitely
           been painted before revealing the starting layout.
        */
        requestAnimationFrame(() => {
            container.classList.remove("pans-page__assigned-pans--loading");
            state.startupInProgress = false;
        });
    },

    getState(container) {
        container._panLayoutState ??= {
            resizeSettleTimer: 0,
            updateRaf: 0,
            textFitRaf: 0,
            startupComplete: false,
            startupInProgress: false,
            dotNetRef: null,
            pendingDrag: null,
            drag: null,
            dragBlockers: [],
            layoutItems: []
        };

        return container._panLayoutState;
    },

    update(container) {
        if (!container) return;

        const items = this.getItems(container);
        const state = this.getState(container);

        if (items.length === 0) {
            state.layoutItems = [];
            return;
        }

        const bounds = container.getBoundingClientRect();

        if (bounds.width <= 0 || bounds.height <= 0) {
            state.layoutItems = [];
            return;
        }

        const gaps = this.getGaps(container);
        const minPanWidth = this.numberFromDataset(container, "minPanWidth", 260);
        const minPanHeight = this.numberFromDataset(container, "minPanHeight", 260);
        const aspects = items.map(item => this.getPanAspect(item));

        const layout = this.findBestLayout(
            aspects,
            bounds.width,
            bounds.height,
            gaps.column,
            gaps.row,
            minPanWidth,
            minPanHeight
        );

        container.dataset.rows = String(layout.rows.length);

        this.applyLayout(
            items,
            layout.rows,
            gaps.column,
            gaps.row,
            bounds.width,
            bounds.height
        );
    },

    fitAllPanText(container) {
        const items = this.getItems(container);

        const sizes = items
            .map(item => this.measurePanTextSize(item, container))
            .filter(size => Number.isFinite(size));

        if (sizes.length === 0) {
            return;
        }

        const sharedSize = Math.min(...sizes);

        for (const item of items) {
            const copy = item.querySelector(SELECTORS.copy);

            copy?.style.setProperty(
                "--pan-copy-value-font-size",
                `${sharedSize}px`
            );
        }
    },

    applyLayout(items, rows, columnGap, rowGap, containerWidth, containerHeight) {
        const state = items[0]
            ? this.getState(items[0].parentElement)
            : null;

        const layoutItems = [];
        let itemIndex = 0;
        let y = 0;

        for (const row of rows) {
            let x = 0;

            for (let i = 0; i < row.count; i++) {
                const item = items[itemIndex + i];
                const width = row.widths[i];
                const left = this.toPercent(x, containerWidth);
                const top = this.toPercent(y, containerHeight);
                const itemWidth = this.toPercent(width, containerWidth);
                const itemHeight = this.toPercent(row.height, containerHeight);

                Object.assign(item.style, {
                    position: "absolute",
                    left: `${left}%`,
                    top: `${top}%`,
                    width: `${itemWidth}%`,
                    height: `${itemHeight}%`
                });

                layoutItems.push({
                    item,
                    left,
                    top,
                    right: left + itemWidth,
                    bottom: top + itemHeight,
                    width: itemWidth,
                    height: itemHeight,
                    centerX: left + itemWidth / 2,
                    centerY: top + itemHeight / 2
                });

                x += width + columnGap;
            }

            itemIndex += row.count;
            y += row.height + rowGap;
        }

        if (state) {
            state.layoutItems = layoutItems;
        }
    },

    toPercent(value, total) {
        return total > 0
            ? value / total * 100
            : 0;
    },

    getItems(container) {
        return [...container.querySelectorAll(SELECTORS.item)];
    },

    attachDragHandlers(container) {
        if (container._panLayoutPointerDown) return;

        container._panLayoutPointerDown = event => this.onPointerDown(container, event);
        container.addEventListener("pointerdown", container._panLayoutPointerDown, { capture: true });
    },

    detachDragHandlers(container) {
        if (!container?._panLayoutPointerDown) return;

        container.removeEventListener("pointerdown", container._panLayoutPointerDown, { capture: true });
        delete container._panLayoutPointerDown;
    },

    onPointerDown(container, event) {
        if (event.button !== 0 || event.pointerType === "mouse" && event.buttons !== 1) return;
        if (event.target?.closest?.(SELECTORS.noDrag)) return;

        const item = event.target?.closest?.(SELECTORS.item);
        if (!item || item.parentElement !== container) return;

        const state = this.getState(container);
        if (state.drag || state.pendingDrag) return;

        state.pendingDrag = {
            pointerId: event.pointerId,
            item,
            startX: event.clientX,
            startY: event.clientY,
            move: moveEvent => this.onPendingPointerMove(container, moveEvent),
            up: upEvent => this.onPendingPointerUp(container, upEvent)
        };

        window.addEventListener("pointermove", state.pendingDrag.move, { capture: true });
        window.addEventListener("pointerup", state.pendingDrag.up, { capture: true });
        window.addEventListener("pointercancel", state.pendingDrag.up, { capture: true });
    },

    onPendingPointerMove(container, event) {
        const state = this.getState(container);
        const pending = state.pendingDrag;

        if (!pending || event.pointerId !== pending.pointerId) return;

        const dx = event.clientX - pending.startX;
        const dy = event.clientY - pending.startY;
        const threshold = this.numberFromDataset(container, "dragStartThresholdPx", DEFAULTS.dragStartThresholdPx);

        if (Math.hypot(dx, dy) < threshold) return;

        event.preventDefault();
        event.stopImmediatePropagation();

        this.startDrag(container, pending, event);
        this.cancelPendingDrag(container);
        this.moveDrag(container, event);
    },

    onPendingPointerUp(container, event) {
        const state = this.getState(container);
        const pending = state.pendingDrag;
        if (!pending || event.pointerId !== pending.pointerId) return;

        this.cancelPendingDrag(container);
    },

    cancelPendingDrag(container) {
        const state = this.getState(container);
        const pending = state.pendingDrag;
        if (!pending) return;

        window.removeEventListener("pointermove", pending.move, { capture: true });
        window.removeEventListener("pointerup", pending.up, { capture: true });
        window.removeEventListener("pointercancel", pending.up, { capture: true });
        state.pendingDrag = null;
    },

    startDrag(container, pending, event) {
        const item = pending.item;
        const rect = item.getBoundingClientRect();
        const state = this.getState(container);

        state.drag = {
            pointerId: pending.pointerId,
            item,
            originalParent: container,
            originalNextSibling: item.nextSibling,
            originalStyle: item.getAttribute("style") ?? "",
            offsetX: event.clientX - rect.left,
            offsetY: event.clientY - rect.top,
            width: rect.width,
            height: rect.height,
            dropTarget: null,
            dropEdge: null,
            move: moveEvent => this.onDragPointerMove(container, moveEvent),
            up: upEvent => this.onDragPointerUp(container, upEvent)
        };

        container.classList.add("pans-page__assigned-pans--dragging");
        item.classList.add("pans-page__assigned-pan--dragging");

        Object.assign(item.style, {
            position: "fixed",
            left: `${rect.left}px`,
            top: `${rect.top}px`,
            width: `${rect.width}px`,
            height: `${rect.height}px`,
            margin: "0",
            transform: "none"
        });

        document.body.appendChild(item);
        this.installDragBlockers(container);

        this.update(container);
        this.requestUpdate(container);

        window.addEventListener("pointermove", state.drag.move, { capture: true });
        window.addEventListener("pointerup", state.drag.up, { capture: true });
        window.addEventListener("pointercancel", state.drag.up, { capture: true });
    },

    onDragPointerMove(container, event) {
        const drag = this.getState(container).drag;
        if (!drag || event.pointerId !== drag.pointerId) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        this.moveDrag(container, event);
    },

    onDragPointerUp(container, event) {
        const drag = this.getState(container).drag;
        if (!drag || event.pointerId !== drag.pointerId) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        this.endDrag(container, { commit: event.type !== "pointercancel" });
    },

    moveDrag(container, event) {
        const drag = this.getState(container).drag;
        if (!drag) return;

        Object.assign(drag.item.style, {
            left: `${event.clientX - drag.offsetX}px`,
            top: `${event.clientY - drag.offsetY}px`
        });

        const drop = this.findNearestDropEdge(container, event.clientX, event.clientY);
        this.setDropIndicator(container, drop);

        drag.dropTarget = drop?.item ?? null;
        drag.dropEdge = drop?.edge ?? null;
    },

    endDrag(container, options = { commit: true }) {
        const state = this.getState(container);
        const drag = state.drag;
        if (!drag) return;

        window.removeEventListener("pointermove", drag.move, { capture: true });
        window.removeEventListener("pointerup", drag.up, { capture: true });
        window.removeEventListener("pointercancel", drag.up, { capture: true });

        this.removeDragBlockers(container);
        this.setDropIndicator(container, null);

        const target = options.commit ? drag.dropTarget : null;
        const edge = options.commit ? drag.dropEdge : null;

        drag.item.classList.remove("pans-page__assigned-pan--dragging");
        drag.item.setAttribute("style", drag.originalStyle);

        if (target && target.parentElement === container) {
            if (edge === "after" || edge === "below") {
                container.insertBefore(drag.item, target.nextSibling);
            } else {
                container.insertBefore(drag.item, target);
            }
        } else {
            container.insertBefore(drag.item, drag.originalNextSibling);
        }

        container.classList.remove("pans-page__assigned-pans--dragging");
        state.drag = null;

        this.requestUpdate(container);

        if (options.commit && target) {
            this.notifyReordered(container);
        }
    },

    findNearestDropEdge(container, clientX, clientY) {
        let best = null;

        for (const item of this.getItems(container)) {
            const rect = item.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;

            const distances = [
                { edge: "before", value: Math.abs(clientX - rect.left), bias: this.distanceToSpan(clientY, rect.top, rect.bottom) },
                { edge: "after", value: Math.abs(clientX - rect.right), bias: this.distanceToSpan(clientY, rect.top, rect.bottom) },
                { edge: "above", value: Math.abs(clientY - rect.top), bias: this.distanceToSpan(clientX, rect.left, rect.right) },
                { edge: "below", value: Math.abs(clientY - rect.bottom), bias: this.distanceToSpan(clientX, rect.left, rect.right) }
            ];

            for (const distance of distances) {
                const score = distance.value + distance.bias * 0.85;
                if (!best || score < best.score) {
                    best = { item, edge: distance.edge, score };
                }
            }
        }

        if (!best) {
            return null;
        }

        const containerRect = container.getBoundingClientRect();
        const pointer = {
            x: this.toPercent(clientX - containerRect.left, containerRect.width),
            y: this.toPercent(clientY - containerRect.top, containerRect.height)
        };

        const adjacent = this.findAdjacentDropItem(container, best.item, best.edge, pointer);

        return {
            ...best,
            adjacentItem: adjacent?.item ?? null,
            adjacentEdge: adjacent?.edge ?? null
        };
    },

    findAdjacentDropItem(container, target, edge, pointer = null) {
        const state = this.getState(container);
        const layoutItems = state.layoutItems ?? [];
        const targetLayout = layoutItems.find(entry => entry.item === target);

        if (!targetLayout) {
            return null;
        }

        const oppositeEdge = {
            before: "after",
            after: "before",
            above: "below",
            below: "above"
        }[edge];

        let best = null;

        for (const candidate of layoutItems) {
            if (candidate.item === target || candidate.item.parentElement !== container) continue;

            const metrics = this.getAdjacencyMetrics(targetLayout, candidate, edge, pointer);
            if (!metrics) continue;

            /*
               One edge can have more than one valid neighbour. Use the pointer's
               position along the shared overlap segment, rather than the target
               centre, so a wide pan above/below multiple smaller pans picks the
               pan that is actually under the cursor.
            */
            const score = metrics.distance * 10000
                + metrics.pointerDistance * 1000
                + metrics.centerDistance
                - metrics.overlapSize * 0.5;

            if (!best || score < best.score) {
                best = {
                    item: candidate.item,
                    edge: oppositeEdge,
                    score
                };
            }
        }

        return best;
    },

    getAdjacencyMetrics(target, candidate, edge, pointer = null) {
        if (edge === "before" || edge === "after") {
            const distance = edge === "before"
                ? target.left - candidate.right
                : candidate.left - target.right;

            if (distance < -0.001) return null;

            const overlap = this.getOverlapSpan(
                target.top,
                target.bottom,
                candidate.top,
                candidate.bottom
            );

            if (!overlap) return null;

            const pointerY = pointer?.y ?? target.centerY;

            return {
                distance,
                overlapSize: overlap.size,
                pointerDistance: this.distanceToSpan(pointerY, overlap.start, overlap.end),
                centerDistance: Math.abs(pointerY - candidate.centerY)
            };
        }

        const distance = edge === "above"
            ? target.top - candidate.bottom
            : candidate.top - target.bottom;

        if (distance < -0.001) return null;

        const overlap = this.getOverlapSpan(
            target.left,
            target.right,
            candidate.left,
            candidate.right
        );

        if (!overlap) return null;

        const pointerX = pointer?.x ?? target.centerX;

        return {
            distance,
            overlapSize: overlap.size,
            pointerDistance: this.distanceToSpan(pointerX, overlap.start, overlap.end),
            centerDistance: Math.abs(pointerX - candidate.centerX)
        };
    },

    getOverlapSpan(aStart, aEnd, bStart, bEnd) {
        const start = Math.max(aStart, bStart);
        const end = Math.min(aEnd, bEnd);
        const size = end - start;

        return size > 0.001
            ? { start, end, size }
            : null;
    },

    getOverlapSize(aStart, aEnd, bStart, bEnd) {
        return Math.max(0, Math.min(aEnd, bEnd) - Math.max(aStart, bStart));
    },

    distanceToSpan(value, min, max) {
        if (value < min) return min - value;
        if (value > max) return value - max;
        return 0;
    },

    setDropIndicator(container, drop) {
        const classes = [
            "pans-page__assigned-pan--drop-before",
            "pans-page__assigned-pan--drop-after",
            "pans-page__assigned-pan--drop-above",
            "pans-page__assigned-pan--drop-below"
        ];

        for (const item of this.getItems(container)) {
            item.classList.remove(...classes);
        }

        if (!drop?.item || !drop.edge) return;

        drop.item.classList.add(`pans-page__assigned-pan--drop-${drop.edge}`);

        if (drop.adjacentItem && drop.adjacentEdge) {
            drop.adjacentItem.classList.add(`pans-page__assigned-pan--drop-${drop.adjacentEdge}`);
        }
    },

    installDragBlockers(container) {
        const state = this.getState(container);
        this.removeDragBlockers(container);

        const blocker = event => {
            if (!this.getState(container).drag) return;
            event.preventDefault();
            event.stopImmediatePropagation();
        };

        const types = [
            "click",
            "dblclick",
            "contextmenu",
            "dragstart",
            "selectstart",
            "pointerover",
            "pointerout",
            "pointerenter",
            "pointerleave",
            "mouseover",
            "mouseout",
            "mouseenter",
            "mouseleave"
        ];

        state.dragBlockers = types.map(type => {
            window.addEventListener(type, blocker, { capture: true });
            return { type, blocker };
        });
    },

    removeDragBlockers(container) {
        const state = this.getState(container);

        for (const { type, blocker } of state.dragBlockers ?? []) {
            window.removeEventListener(type, blocker, { capture: true });
        }

        state.dragBlockers = [];
    },

    notifyReordered(container) {
        const ids = this.getItems(container)
            .map(item => item.dataset.panId)
            .filter(Boolean);

        const dotNetRef = this.getState(container).dotNetRef;
        dotNetRef?.invokeMethodAsync?.("NotifyPansReordered", ids);
    },

    getGaps(element) {
        const styles = getComputedStyle(element);
        const gap = parseFloat(styles.gap) || 0;

        return {
            column: parseFloat(styles.columnGap) || gap,
            row: parseFloat(styles.rowGap) || gap
        };
    },

    numberFromDataset(element, key, fallback) {
        const value = Number(element?.dataset?.[key]);
        return Number.isFinite(value) ? value : fallback;
    },

    getPanAspect(item) {
        if (item._panAspect) return item._panAspect;

        const viewBox = item.querySelector(SELECTORS.svg)?.viewBox?.baseVal;

        item._panAspect = viewBox?.width > 0 && viewBox?.height > 0
            ? viewBox.width / viewBox.height
            : 1;

        return item._panAspect;
    },

    findBestLayout(aspects, width, height, columnGap, rowGap, minPanWidth, minPanHeight) {
        let bestRows = null;
        let bestScore = -Infinity;

        for (const partition of this.getLayoutPartitions(aspects.length)) {
            const rows = this.createRows(partition, aspects, width, columnGap);
            const availableHeight = height - rowGap * (rows.length - 1);

            if (availableHeight <= 0) continue;

            const naturalHeightTotal = rows.reduce((sum, row) => sum + row.naturalHeight, 0);
            if (naturalHeightTotal <= 0) continue;

            const scale = Math.min(1, availableHeight / naturalHeightTotal);

            if (!this.resolveRows(rows, scale, minPanWidth, minPanHeight)) {
                continue;
            }

            const score = this.scoreLayout(rows, width, availableHeight, aspects.length);

            if (score > bestScore) {
                bestScore = score;
                bestRows = rows;
            }
        }

        return bestRows
            ? { rows: bestRows }
            : this.findFallbackLayout(aspects, width, height, columnGap, rowGap);
    },

    createRows(partition, aspects, width, columnGap) {
        return partition.map(row => {
            const rowAspects = aspects.slice(row.start, row.start + row.count);
            const aspectTotal = rowAspects.reduce((sum, aspect) => sum + aspect, 0);
            const availableWidth = width - columnGap * (row.count - 1);

            return {
                ...row,
                aspects: rowAspects,
                aspectTotal,
                availableWidth,
                naturalHeight: availableWidth / aspectTotal
            };
        });
    },

    resolveRows(rows, scale, minPanWidth, minPanHeight) {
        for (const row of rows) {
            row.height = row.naturalHeight * scale;
            row.widths = row.aspects.map(aspect => row.availableWidth * aspect / row.aspectTotal);

            if (row.height < minPanHeight || row.widths.some(width => width < minPanWidth)) {
                return false;
            }
        }

        return true;
    },

    scoreLayout(rows, width, availableHeight, totalCount) {
        const usedHeight = rows.reduce((sum, row) => sum + row.height, 0);
        const renderedArea = rows.reduce((sum, row) => {
            return sum + row.widths.reduce((rowSum, itemWidth) => rowSum + itemWidth * row.height, 0);
        }, 0);

        const unusedHeight = availableHeight - usedHeight;

        return renderedArea
            - unusedHeight * width * 0.35
            - this.getRowBalancePenalty(rows) * width * 12
            - this.getLonelyRowPenalty(rows, totalCount) * renderedArea
            - this.getHeightVariancePenalty(rows) * width * 8;
    },

    getLayoutPartitions(count) {
        if (this._layoutCache.has(count)) {
            return this._layoutCache.get(count);
        }

        const layouts = [];

        const build = (start, rows) => {
            if (start >= count) {
                layouts.push([...rows]);
                return;
            }

            for (let end = start + 1; end <= count; end++) {
                rows.push({ start, count: end - start });
                build(end, rows);
                rows.pop();
            }
        };

        build(0, []);
        this._layoutCache.set(count, layouts);

        return layouts;
    },

    findFallbackLayout(aspects, width, height, columnGap, rowGap) {
        const count = aspects.length;
        const cols = Math.ceil(Math.sqrt(count));
        const rowCount = Math.ceil(count / cols);
        const availableRowHeight = (height - rowGap * (rowCount - 1)) / rowCount;

        const rows = [];
        let start = 0;

        for (let rowIndex = 0; rowIndex < rowCount; rowIndex++) {
            const rowItemCount = Math.min(cols, count - start);
            const rowAspects = aspects.slice(start, start + rowItemCount);
            const aspectTotal = rowAspects.reduce((sum, aspect) => sum + aspect, 0);
            const availableWidth = width - columnGap * (rowItemCount - 1);

            rows.push({
                start,
                count: rowItemCount,
                aspects: rowAspects,
                aspectTotal,
                availableWidth,
                height: availableRowHeight,
                widths: rowAspects.map(aspect => availableWidth * aspect / aspectTotal)
            });

            start += rowItemCount;
        }

        return { rows };
    },

    measurePanTextSize(item, container) {
        const parts = this.getTextFitParts(item);
        if (!parts) return null;

        const itemRect = item.getBoundingClientRect();
        const copyRect = parts.copy.getBoundingClientRect();

        if (itemRect.width <= 0 || copyRect.height <= 0) {
            return null;
        }

        const style = getComputedStyle(parts.copy);
        const gaps = this.getGaps(parts.copy);

        const leftInset = parseFloat(style.left) || 0;
        const rightInset = parseFloat(style.right) || 0;
        const gap = gaps.column || gaps.row || 0;

        const fallbackAvailable =
            Math.max(0, (itemRect.width - leftInset - rightInset - gap) / 2);

        const clearance =
            this.numberFromDataset(container, "panTextClearance", 6);

        const y = copyRect.top + copyRect.height / 2;

        const panBounds =
            this.getEstimatedPanBoundsAtY(parts.svg, y);

        const svgLeft = panBounds.left - itemRect.left;
        const svgRight = panBounds.right - itemRect.left;

        const leftAvailable = Math.max(
            0,
            svgLeft - leftInset - clearance
        );

        const rightAvailable = Math.max(
            0,
            itemRect.width - rightInset - svgRight - clearance
        );

        const minimumReadableWidth = Math.min(72, fallbackAvailable);

        return this.measureCopyFit([
            {
                group: parts.leftGroup,
                availableWidth: leftAvailable > 0
                    ? Math.min(leftAvailable, fallbackAvailable)
                    : minimumReadableWidth
            },
            {
                group: parts.rightGroup,
                availableWidth: rightAvailable > 0
                    ? Math.min(rightAvailable, fallbackAvailable)
                    : minimumReadableWidth
            }
        ]);
    },

    getTextFitParts(item) {
        const svg = item.querySelector(SELECTORS.svg);
        const copy = item.querySelector(SELECTORS.copy);
        const leftGroup = item.querySelector(SELECTORS.leftGroup);
        const rightGroup = item.querySelector(SELECTORS.rightGroup);

        if (!svg || !copy || !leftGroup || !rightGroup) {
            return null;
        }

        return { svg, copy, leftGroup, rightGroup };
    },

    measureCopyFit(groups) {
        const resolved = groups
            .map(({ group, availableWidth }) => ({
                group,
                label: group?.querySelector(SELECTORS.label),
                value: group?.querySelector(SELECTORS.value),
                availableWidth: Math.max(0, availableWidth)
            }))
            .filter(item => item.group && item.value);

        if (resolved.length === 0) {
            return null;
        }

        for (const item of resolved) {
            item.group.style.setProperty(
                "--pan-copy-group-width",
                `${item.availableWidth}px`
            );
        }

        const baseSize = Math.min(
            ...resolved.map(item =>
                this.getBaseFontSize(item.value, "panBaseValueFontSize")
            )
        );

        const minSize = 7;
        let fittedSize = baseSize;

        for (const item of resolved) {
            fittedSize = Math.min(
                fittedSize,
                this.getFittedFontSize(
                    item.value,
                    item.availableWidth,
                    baseSize,
                    minSize
                )
            );

            if (item.label) {
                fittedSize = Math.min(
                    fittedSize,
                    this.getFittedFontSize(
                        item.label,
                        item.availableWidth,
                        baseSize * 0.72,
                        6
                    ) / 0.72
                );
            }
        }

        return Math.max(minSize, fittedSize);
    },

    getBaseFontSize(element, cacheKey) {
        if (!element) return 12;

        element.dataset[cacheKey] ??= String(parseFloat(getComputedStyle(element).fontSize));

        return Number(element.dataset[cacheKey]);
    },

    getFittedFontSize(element, availableWidth, baseSize, minSize) {
        if (!element || availableWidth <= 0) return minSize;

        const text = element.textContent?.trim() ?? "";
        if (!text) return baseSize;

        const measuredWidth = this.measureTextWidth(text, getComputedStyle(element), baseSize);

        return measuredWidth <= availableWidth
            ? baseSize
            : Math.max(minSize, baseSize * availableWidth / measuredWidth);
    },

    measureTextWidth(text, style, fontSize) {
        this._textMeasureCanvas ??= document.createElement("canvas");

        const context = this._textMeasureCanvas.getContext("2d");
        const fontStyle = style.fontStyle || "normal";
        const fontWeight = style.fontWeight || "400";
        const fontFamily = style.fontFamily || "sans-serif";

        context.font = `${fontStyle} ${fontWeight} ${fontSize}px ${fontFamily}`;

        const measuredText = style.textTransform === "uppercase"
            ? text.toUpperCase()
            : text;

        const letterSpacing = parseFloat(style.letterSpacing) || 0;

        return context.measureText(measuredText).width
            + Math.max(0, measuredText.length - 1) * letterSpacing;
    },

    getEstimatedPanBoundsAtY(svg, clientY) {
        const svgRect = svg.getBoundingClientRect();
        const ctm = svg.getScreenCTM();

        if (!ctm || svgRect.width <= 0 || svgRect.height <= 0) {
            return { left: svgRect.left, right: svgRect.right };
        }

        const cacheKey = [
            Math.round(svgRect.width),
            Math.round(svgRect.height),
            Math.round(clientY - svgRect.top)
        ].join(":");

        svg._panBoundsCache ??= new Map();

        const cached = svg._panBoundsCache.get(cacheKey);
        if (cached) {
            return {
                left: svgRect.left + cached.leftOffset,
                right: svgRect.left + cached.rightOffset
            };
        }

        if (svg._panBoundsCache.size > 80) {
            svg._panBoundsCache.clear();
        }

        const bounds = this.samplePanBoundsAtY(svg, svgRect, ctm, clientY);

        svg._panBoundsCache.set(cacheKey, {
            leftOffset: bounds.left - svgRect.left,
            rightOffset: bounds.right - svgRect.left
        });

        return bounds;
    },

    samplePanBoundsAtY(svg, svgRect, ctm, clientY) {
        const inverse = ctm.inverse();

        const leftPoint = new DOMPoint(svgRect.left, clientY).matrixTransform(inverse);
        const rightPoint = new DOMPoint(svgRect.right, clientY).matrixTransform(inverse);

        const svgY = leftPoint.y;
        const minSvgX = Math.min(leftPoint.x, rightPoint.x);
        const maxSvgX = Math.max(leftPoint.x, rightPoint.x);

        const hitElements = this.getPanHitElements(svg);
        if (hitElements.length === 0) {
            return { left: svgRect.left, right: svgRect.right };
        }

        const sampleCount = this.numberFromDataset(svg, "panBoundsSamples", 72);
        const point = new DOMPoint();

        let minHitX = null;
        let maxHitX = null;

        for (let i = 0; i <= sampleCount; i++) {
            point.x = minSvgX + (maxSvgX - minSvgX) * i / sampleCount;
            point.y = svgY;

            if (!this.pointHitsAnyElement(point, hitElements)) continue;

            minHitX ??= point.x;
            maxHitX = point.x;
        }

        if (minHitX === null || maxHitX === null) {
            const center = svgRect.left + svgRect.width / 2;
            return { left: center, right: center };
        }

        const leftClient = new DOMPoint(minHitX, svgY).matrixTransform(ctm);
        const rightClient = new DOMPoint(maxHitX, svgY).matrixTransform(ctm);

        return {
            left: Math.min(leftClient.x, rightClient.x),
            right: Math.max(leftClient.x, rightClient.x)
        };
    },

    getPanHitElements(svg) {
        svg._panHitElements ??= [...svg.querySelectorAll(SELECTORS.panShape)]
            .filter(element =>
                typeof element.isPointInFill === "function" ||
                typeof element.isPointInStroke === "function"
            );

        return svg._panHitElements;
    },

    clearLayoutCaches(container) {
        for (const item of container.querySelectorAll(SELECTORS.item)) {
            delete item._panAspect;
        }

        for (const svg of container.querySelectorAll(SELECTORS.svg)) {
            delete svg._panHitElements;
            svg._panBoundsCache?.clear();
        }
    },

    pointHitsAnyElement(point, elements) {
        return elements.some(element => {
            try {
                return element.isPointInFill?.(point) || element.isPointInStroke?.(point);
            } catch {
                return false;
            }
        });
    },

    getRowBalancePenalty(rows) {
        if (rows.length <= 1) return 0;

        const average = rows.reduce((sum, row) => sum + row.count, 0) / rows.length;

        return rows.reduce((sum, row) => sum + Math.abs(row.count - average), 0);
    },

    getLonelyRowPenalty(rows, totalCount) {
        if (totalCount <= 2) return 0;

        return rows.filter(row => row.count === 1).length * 0.08;
    },

    getHeightVariancePenalty(rows) {
        if (rows.length <= 1) return 0;

        const average = rows.reduce((sum, row) => sum + row.height, 0) / rows.length;

        return rows.reduce((sum, row) => {
            return sum + Math.abs(row.height - average) / average;
        }, 0);
    }
};