const SELECTORS = {
    item: ".pans-page__assigned-pan",
    svg: ".sp-svg",
    copy: ".pans-page__assigned-pan-copy",
    leftGroup: ".pans-page__assigned-pan-copy-group--left",
    rightGroup: ".pans-page__assigned-pan-copy-group--right",
    label: ".pans-page__assigned-pan-copy-label",
    value: ".pans-page__assigned-pan-track, .pans-page__assigned-pan-type",
    panShape: ".sp-svg-circle, .sp-note, path, circle, ellipse, rect, polygon, polyline"
};

const DEFAULTS = {
    resizeUpdateIntervalMs: 0,
    resizeSettleDelayMs: 2000,
    resizeWidthThresholdPx: 10,
    resizeHeightThresholdPx: 10,
    textFitWidthThresholdPx: 20,
    textFitHeightThresholdPx: 20
};

window.panLayout = {
    _layoutCache: new Map(),
    _textMeasureCanvas: null,

    observe(container) {
        if (!container) return;

        this.disconnect(container);

        container._panLayoutState = {
            raf: 0,
            timer: 0,
            settleTimer: 0,
            lastRunAt: 0,
            pendingForce: false,
            pendingReason: null,
            lastLayoutWidth: null,
            lastLayoutHeight: null,
            lastTextWidth: null,
            lastTextHeight: null,
            lastItemCount: null,
            running: false
        };

        const requestResize = () => {
            this.requestUpdate(container, { reason: "resize" });
        };

        const requestMutation = () => {
            this.clearItemCaches(container);
            this.requestUpdate(container, { force: true, reason: "mutation" });
        };

        container._panGridObserver = new ResizeObserver(requestResize);
        container._panGridObserver.observe(container);

        container._panMutationObserver = new MutationObserver(requestMutation);
        container._panMutationObserver.observe(container, {
            childList: true,
            subtree: true
        });

        this.requestUpdate(container, { force: true, reason: "observe" });
    },

    disconnect(container) {
        if (!container) return;

        container._panGridObserver?.disconnect();
        container._panMutationObserver?.disconnect();

        const state = container._panLayoutState;
        if (state) {
            cancelAnimationFrame(state.raf);
            clearTimeout(state.timer);
            clearTimeout(state.settleTimer);
        }

        cancelAnimationFrame(container._panGridRaf);
        delete container._panLayoutState;
    },

    requestUpdate(container, options = {}) {
        if (!container) return;

        const state = this.getState(container);
        const force = options.force === true;
        const reason = options.reason ?? "manual";

        state.pendingForce ||= force;
        state.pendingReason = reason;

        clearTimeout(state.settleTimer);

        if (reason === "resize") {
            state.settleTimer = setTimeout(() => {
                this.requestUpdate(container, { force: true, reason: "resize-settled" });
            }, this.numberFromDataset(container, "resizeSettleDelayMs", DEFAULTS.resizeSettleDelayMs));
        }

        if (!force && !this.hasSignificantContainerChange(container, state)) {
            return;
        }

        const now = performance.now();
        const interval = this.numberFromDataset(
            container,
            "resizeUpdateIntervalMs",
            DEFAULTS.resizeUpdateIntervalMs
        );
        const elapsed = now - state.lastRunAt;

        if (!force && elapsed < interval) {
            clearTimeout(state.timer);
            state.timer = setTimeout(() => {
                this.scheduleUpdate(container);
            }, interval - elapsed);
            return;
        }

        this.scheduleUpdate(container);
    },

    scheduleUpdate(container) {
        const state = this.getState(container);

        if (state.raf) {
            return;
        }

        state.raf = requestAnimationFrame(() => {
            state.raf = 0;
            this.runUpdate(container);
        });
    },

    runUpdate(container) {
        if (!container) return;

        const state = this.getState(container);
        if (state.running) return;

        state.running = true;
        state.lastRunAt = performance.now();

        container.classList.add("pans-page__assigned-pans--laying-out");

        const force = state.pendingForce;
        const bounds = container.getBoundingClientRect();
        const itemCount = this.getItems(container).length;

        state.pendingForce = false;
        state.pendingReason = null;

        const shouldLayout = force || this.hasSignificantLayoutChange(container, state, bounds, itemCount);

        if (shouldLayout) {
            this.update(container, bounds);
            state.lastLayoutWidth = bounds.width;
            state.lastLayoutHeight = bounds.height;
            state.lastItemCount = itemCount;
        }

        requestAnimationFrame(() => {
            const textBounds = container.getBoundingClientRect();
            const shouldFitText = force
                || shouldLayout
                || this.hasSignificantTextFitChange(container, state, textBounds);

            if (shouldFitText) {
                this.fitAllPanText(container);
                state.lastTextWidth = textBounds.width;
                state.lastTextHeight = textBounds.height;
            }

            container.classList.remove("pans-page__assigned-pans--laying-out");
            state.running = false;
        });
    },

    getState(container) {
        container._panLayoutState ??= {
            raf: 0,
            timer: 0,
            settleTimer: 0,
            lastRunAt: 0,
            pendingForce: false,
            pendingReason: null,
            lastLayoutWidth: null,
            lastLayoutHeight: null,
            lastTextWidth: null,
            lastTextHeight: null,
            lastItemCount: null,
            running: false
        };

        return container._panLayoutState;
    },

    hasSignificantContainerChange(container, state) {
        const bounds = container.getBoundingClientRect();
        const itemCount = container.querySelectorAll(SELECTORS.item).length;

        return this.hasSignificantLayoutChange(container, state, bounds, itemCount);
    },

    hasSignificantLayoutChange(container, state, bounds, itemCount) {
        if (bounds.width <= 0 || bounds.height <= 0) return false;
        if (state.lastLayoutWidth === null || state.lastLayoutHeight === null) return true;
        if (state.lastItemCount !== itemCount) return true;

        const widthThreshold = this.numberFromDataset(
            container,
            "resizeWidthThresholdPx",
            DEFAULTS.resizeWidthThresholdPx
        );
        const heightThreshold = this.numberFromDataset(
            container,
            "resizeHeightThresholdPx",
            DEFAULTS.resizeHeightThresholdPx
        );

        return Math.abs(bounds.width - state.lastLayoutWidth) >= widthThreshold
            || Math.abs(bounds.height - state.lastLayoutHeight) >= heightThreshold;
    },

    hasSignificantTextFitChange(container, state, bounds) {
        if (bounds.width <= 0 || bounds.height <= 0) return false;
        if (state.lastTextWidth === null || state.lastTextHeight === null) return true;

        const widthThreshold = this.numberFromDataset(
            container,
            "textFitWidthThresholdPx",
            DEFAULTS.textFitWidthThresholdPx
        );
        const heightThreshold = this.numberFromDataset(
            container,
            "textFitHeightThresholdPx",
            DEFAULTS.textFitHeightThresholdPx
        );

        return Math.abs(bounds.width - state.lastTextWidth) >= widthThreshold
            || Math.abs(bounds.height - state.lastTextHeight) >= heightThreshold;
    },

    update(container, bounds = null) {
        if (!container) return;

        const items = this.getItems(container);
        if (items.length === 0) return;

        bounds ??= container.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return;

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
        this.applyLayout(items, layout.rows, gaps.column, gaps.row);
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

    applyLayout(items, rows, columnGap, rowGap) {
        let itemIndex = 0;
        let y = 0;

        for (const row of rows) {
            let x = 0;

            for (let i = 0; i < row.count; i++) {
                const item = items[itemIndex + i];
                const width = row.widths[i];

                Object.assign(item.style, {
                    position: "absolute",
                    left: `${x}px`,
                    top: `${y}px`,
                    width: `${width}px`,
                    height: `${row.height}px`
                });

                x += width + columnGap;
            }

            itemIndex += row.count;
            y += row.height + rowGap;
        }
    },

    getItems(container) {
        return [...container.querySelectorAll(SELECTORS.item)];
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
        const viewBox = item.querySelector(SELECTORS.svg)?.viewBox?.baseVal;

        return viewBox?.width > 0 && viewBox?.height > 0
            ? viewBox.width / viewBox.height
            : 1;
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
            Math.round(svgRect.width / 4) * 4,
            Math.round(svgRect.height / 4) * 4,
            Math.round((clientY - svgRect.top) / 4) * 4
        ].join(":");

        svg._panBoundsCache ??= new Map();

        const cached = svg._panBoundsCache.get(cacheKey);
        if (cached) {
            return {
                left: svgRect.left + cached.leftOffset,
                right: svgRect.left + cached.rightOffset
            };
        }

        if (svg._panBoundsCache.size > 60) {
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

        const sampleCount = this.numberFromDataset(svg, "panBoundsSamples", 40);
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

    clearItemCaches(container) {
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
