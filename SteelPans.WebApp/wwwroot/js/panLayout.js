window.panLayout = {
    observe(container) {
        if (!container) return;

        const update = () => this.update(container);

        container._panGridObserver?.disconnect();
        container._panGridObserver = new ResizeObserver(update);
        container._panGridObserver.observe(container);

        update();
    },

    update(container) {
        if (!container) return;

        const items = [...container.querySelectorAll(".pans-page__assigned-pan")];
        const count = items.length;

        if (count <= 0) return;

        const rect = container.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;

        const styles = getComputedStyle(container);
        const columnGap = parseFloat(styles.columnGap) || parseFloat(styles.gap) || 0;
        const rowGap = parseFloat(styles.rowGap) || parseFloat(styles.gap) || columnGap || 0;

        const minPanWidth = Number(container.dataset.minPanWidth || 220);
        const minPanHeight = Number(container.dataset.minPanHeight || 220);

        const aspects = items.map(item => this.getPanAspect(item));

        const layout = this.findBestLayout(
            aspects,
            rect.width,
            rect.height,
            columnGap,
            rowGap,
            minPanWidth,
            minPanHeight);

        container.dataset.rows = String(layout.rows.length);

        let itemIndex = 0;
        let y = 0;

        for (const row of layout.rows) {
            let x = 0;

            for (let i = 0; i < row.count; i++) {
                const item = items[itemIndex + i];
                const itemWidth = row.widths[i];

                item.style.position = "absolute";
                item.style.left = `${x}px`;
                item.style.top = `${y}px`;
                item.style.width = `${itemWidth}px`;
                item.style.height = `${row.height}px`;

                this.fitPanText(item);

                x += itemWidth + columnGap;
            }

            itemIndex += row.count;
            y += row.height + rowGap;
        }
    },

    getPanAspect(item) {
        const svg = item.querySelector(".sp-svg");
        const viewBox = svg?.viewBox?.baseVal;

        if (viewBox && viewBox.width > 0 && viewBox.height > 0) {
            return viewBox.width / viewBox.height;
        }

        return 1;
    },

    findBestLayout(aspects, width, height, columnGap, rowGap, minPanWidth, minPanHeight) {
        const count = aspects.length;
        const layouts = [];

        const buildLayouts = (start, rows) => {
            if (start >= count) {
                layouts.push([...rows]);
                return;
            }

            for (let end = start + 1; end <= count; end++) {
                rows.push({
                    start,
                    count: end - start
                });

                buildLayouts(end, rows);
                rows.pop();
            }
        };

        buildLayouts(0, []);

        let bestRows = null;
        let bestScore = -Infinity;

        for (const rows of layouts) {
            const rowData = rows.map(row => {
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

            const availableHeight = height - rowGap * (rowData.length - 1);
            if (availableHeight <= 0) continue;

            const naturalHeightTotal = rowData.reduce((sum, row) => sum + row.naturalHeight, 0);
            if (naturalHeightTotal <= 0) continue;

            const scale = Math.min(1, availableHeight / naturalHeightTotal);

            let isValid = true;
            let usedHeight = 0;
            let renderedArea = 0;

            for (const row of rowData) {
                row.height = row.naturalHeight * scale;

                row.widths = row.aspects.map(aspect => {
                    return row.availableWidth * (aspect / row.aspectTotal);
                });

                if (row.height < minPanHeight || row.widths.some(itemWidth => itemWidth < minPanWidth)) {
                    isValid = false;
                    break;
                }

                usedHeight += row.height;
                renderedArea += row.widths.reduce((sum, itemWidth) => {
                    return sum + itemWidth * row.height;
                }, 0);
            }

            if (!isValid) continue;

            const unusedHeight = availableHeight - usedHeight;
            const rowBalancePenalty = this.getRowBalancePenalty(rowData);
            const lonelyRowPenalty = this.getLonelyRowPenalty(rowData, count);
            const heightVariancePenalty = this.getHeightVariancePenalty(rowData);

            const score =
                renderedArea
                - unusedHeight * width * 0.35
                - rowBalancePenalty * width * 12
                - lonelyRowPenalty * renderedArea
                - heightVariancePenalty * width * 8;

            if (score > bestScore) {
                bestScore = score;
                bestRows = rowData;
            }
        }

        if (bestRows) {
            return { rows: bestRows };
        }

        return this.findFallbackLayout(
            aspects,
            width,
            height,
            columnGap,
            rowGap);
    },

    findFallbackLayout(aspects, width, height, columnGap, rowGap) {
        const count = aspects.length;
        const rows = [];
        const cols = Math.ceil(Math.sqrt(count));
        const rowCount = Math.ceil(count / cols);

        let start = 0;

        for (let rowIndex = 0; rowIndex < rowCount; rowIndex++) {
            const remaining = count - start;
            const rowItemCount = Math.min(cols, remaining);
            const rowAspects = aspects.slice(start, start + rowItemCount);
            const aspectTotal = rowAspects.reduce((sum, aspect) => sum + aspect, 0);
            const availableWidth = width - columnGap * (rowItemCount - 1);
            const availableHeight = (height - rowGap * (rowCount - 1)) / rowCount;

            rows.push({
                start,
                count: rowItemCount,
                aspects: rowAspects,
                aspectTotal,
                availableWidth,
                height: availableHeight,
                widths: rowAspects.map(aspect => availableWidth * (aspect / aspectTotal))
            });

            start += rowItemCount;
        }

        return { rows };
    },

    getRowBalancePenalty(rows) {
        if (rows.length <= 1) return 0;

        const counts = rows.map(row => row.count);
        const average = counts.reduce((sum, count) => sum + count, 0) / counts.length;

        return counts.reduce((sum, count) => {
            return sum + Math.abs(count - average);
        }, 0);
    },

    getLonelyRowPenalty(rows, totalCount) {
        if (totalCount <= 2) return 0;

        const lonelyRows = rows.filter(row => row.count === 1).length;

        return lonelyRows * 0.08;
    },

    getHeightVariancePenalty(rows) {
        if (rows.length <= 1) return 0;

        const average = rows.reduce((sum, row) => sum + row.height, 0) / rows.length;

        return rows.reduce((sum, row) => {
            return sum + Math.abs(row.height - average) / average;
        }, 0);
    },

    fitPanText(item) {
        const svg = item.querySelector(".sp-svg");
        const copy = item.querySelector(".pans-page__assigned-pan-copy");

        const panGroup = item.querySelector(".pans-page__assigned-pan-copy-group--left");
        const trackGroup = item.querySelector(".pans-page__assigned-pan-copy-group--right");

        if (!svg || !copy || !panGroup || !trackGroup) return;

        const itemRect = item.getBoundingClientRect();
        const svgRect = svg.getBoundingClientRect();

        if (itemRect.width <= 0 || svgRect.width <= 0) return;

        const copyStyle = getComputedStyle(copy);

        const leftInset = parseFloat(copyStyle.left) || 0;
        const rightInset = parseFloat(copyStyle.right) || 0;
        const gap = parseFloat(copyStyle.columnGap) || parseFloat(copyStyle.gap) || 0;

        const padding = 6;

        const svgLeft = svgRect.left - itemRect.left;
        const svgRight = svgRect.right - itemRect.left;

        const leftAvailable = Math.max(0, svgLeft - leftInset - padding);
        const rightAvailable = Math.max(0, itemRect.width - rightInset - svgRight - padding);

        const fallbackAvailable =
            Math.max(0, (itemRect.width - leftInset - rightInset - gap) / 2);

        const panAvailableWidth = Math.max(leftAvailable, fallbackAvailable * 0.35);
        const trackAvailableWidth = Math.max(rightAvailable, fallbackAvailable * 0.35);

        this.scalePanCopyToFit([
            { group: panGroup, availableWidth: panAvailableWidth },
            { group: trackGroup, availableWidth: trackAvailableWidth }
        ]);
    },

    scalePanCopyToFit(groups) {
        const resolved = groups
            .map(groupInfo => {
                const group = groupInfo.group;
                const label = group?.querySelector(".pans-page__assigned-pan-copy-label");
                const value =
                    group?.querySelector(".pans-page__assigned-pan-track") ||
                    group?.querySelector(".pans-page__assigned-pan-type");

                return {
                    group,
                    label,
                    value,
                    availableWidth: groupInfo.availableWidth
                };
            })
            .filter(x => x.group && x.value);

        if (resolved.length === 0) return;

        for (const item of resolved) {
            item.group.style.maxWidth = `${item.availableWidth}px`;
            item.label?.style.removeProperty("font-size");
            item.value.style.removeProperty("font-size");
        }

        const baseValueSize = Math.min(
            ...resolved.map(item => parseFloat(getComputedStyle(item.value).fontSize))
        );

        const minValueSize = 7;
        let fittedSize = baseValueSize;

        const overflows = size => {
            for (const item of resolved) {
                item.value.style.fontSize = `${size}px`;

                if (item.label) {
                    item.label.style.fontSize = `${Math.max(6, size * 0.72)}px`;
                }

                const requiredWidth = Math.max(
                    item.label?.scrollWidth ?? 0,
                    item.value.scrollWidth
                );

                if (requiredWidth > item.availableWidth) {
                    return true;
                }
            }

            return false;
        };

        while (fittedSize > minValueSize && overflows(fittedSize)) {
            fittedSize -= 0.25;
        }

        for (const item of resolved) {
            item.value.style.fontSize = `${fittedSize}px`;

            if (item.label) {
                item.label.style.fontSize = `${Math.max(6, fittedSize * 0.72)}px`;
            }
        }
    }
};