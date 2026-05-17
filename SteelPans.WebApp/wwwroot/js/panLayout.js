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

        const items = [...container.querySelectorAll(".assigned-pan, .pans-page__assigned-pan")];
        const count = items.length || Number(container.dataset.panCount || 0);

        if (count <= 0) return;

        const rect = container.getBoundingClientRect();
        const styles = getComputedStyle(container);

        const columnGap = parseFloat(styles.columnGap) || 0;
        const rowGap = parseFloat(styles.rowGap) || columnGap || 0;

        const aspects = items.map(item => {
            const svg = item.querySelector(".sp-svg");
            const viewBox = svg?.viewBox?.baseVal;

            if (viewBox && viewBox.width > 0 && viewBox.height > 0) {
                return viewBox.width / viewBox.height;
            }

            return 1;
        });

        let bestCols = 1;
        let bestScore = -Infinity;

        for (let cols = 1; cols <= count; cols++) {
            const rows = Math.ceil(count / cols);

            const cellWidth = (rect.width - columnGap * (cols - 1)) / cols;
            const cellHeight = (rect.height - rowGap * (rows - 1)) / rows;

            if (cellWidth <= 0 || cellHeight <= 0) continue;

            let totalRenderedArea = 0;
            let totalCellArea = cellWidth * cellHeight * count;

            for (const aspect of aspects) {
                const renderedWidth = Math.min(cellWidth, cellHeight * aspect);
                const renderedHeight = renderedWidth / aspect;

                totalRenderedArea += renderedWidth * renderedHeight;
            }

            const fillRatio = totalRenderedArea / totalCellArea;

            const emptyCells = rows * cols - count;
            const emptyCellPenalty = emptyCells * 0.08;

            const tooManyColumnsPenalty = cols > count ? 1 : 0;

            const score =
                fillRatio
                - emptyCellPenalty
                - tooManyColumnsPenalty;

            if (score > bestScore) {    
                bestScore = score;
                bestCols = cols;
            }
        }

        container.dataset.cols = String(bestCols);
        container.style.setProperty("--pan-cols", bestCols);
    }
};