import { defaults } from "./constants.js";
import { clamp } from "./utils.js";

export function measureLayout(state, durationSeconds) {
    const bounds = state.root?.getBoundingClientRect?.() || { width: 0, height: 0 };
    const rootWidth = Math.max(280, bounds.width || 0);
    const viewportHeight = Math.max(
        260,
        window.visualViewport?.height || window.innerHeight || bounds.height || 0);

    const viewportWidth = Math.max(
        180,
        rootWidth - Math.min(defaults.labelWidth, rootWidth * 0.36));

    const compactness = clamp(
        (Math.min(rootWidth, viewportHeight) - 320) / 720,
        0,
        1);

    const laneHeight = clamp(
        Math.min(rootWidth * 0.45, viewportHeight * 0.2),
        70,
        220);

    return {
        labelWidth: Math.round(clamp(rootWidth * 0.15, 95, 180)),
        rulerHeight: Math.round(clamp(20 + compactness * 8, 20, 40)),
        laneHeight: Math.round(laneHeight),
        noteHeight: Math.round(clamp(laneHeight * 0.08, 5, 14)),
        pixelsPerSecond: clamp(
            viewportWidth / Math.max(4, Math.min(durationSeconds, 8)),
            112,
            defaults.pixelsPerSecond),
        minTimelineWidth: Math.max(Math.round(viewportWidth), 560)
    };
}

export function getNoteTop(state, semitone) {
    const min = Number.isFinite(Number(state.minSemitone)) ? Number(state.minSemitone) : 0;
    const max = Number.isFinite(Number(state.maxSemitone)) ? Number(state.maxSemitone) : min;
    const range = Math.max(1, max - min);
    const availableHeight = state.laneHeight - state.noteHeight - 22;
    const value = clamp(Number(semitone) || min, min, max);
    const normalized = (value - min) / range;

    return (1 - normalized) * availableHeight;
}

export function getSemitoneFromPointerY(state, clientY) {
    const rect = state.tracks.getBoundingClientRect();
    const min = Number.isFinite(Number(state.minSemitone)) ? Number(state.minSemitone) : 0;
    const max = Number.isFinite(Number(state.maxSemitone)) ? Number(state.maxSemitone) : min;
    const range = Math.max(1, max - min);
    const availableHeight = state.laneHeight - state.noteHeight - 22;
    const y = clamp(clientY - rect.top - 11, 0, availableHeight);
    const normalized = 1 - y / availableHeight;

    return Math.round(clamp(min + normalized * range, min, max));
}

export function getNoteWidth(state, durationSeconds) {
    return Math.max(durationSeconds * state.pixelsPerSecond, 8);
}

export function fitNoteLabel(label, width, text, noteHeight) {
    if (!label || !text) {
        if (label)
            label.style.display = "none";
        return;
    }

    const minFontSize = 6;
    const fontSize = Math.min(10.5, noteHeight - 2);

    if (fontSize < minFontSize) {
        label.style.display = "none";
        return;
    }

    const availableWidth = Math.max(0, width - 10);
    const estimatedTextWidth = String(text).length * fontSize * 0.64;

    if (estimatedTextWidth > availableWidth) {
        label.style.display = "none";
        return;
    }

    label.style.fontSize = `${fontSize}px`;
    label.style.width = `${availableWidth}px`;
    label.style.maxWidth = `${availableWidth}px`;
    label.style.display = "block";
}

export function positionBarLabels(state) {
    for (const label of state.barLabels || []) {
        const width = label.element.getBoundingClientRect().width || 0;
        label.element.style.left = `${label.left - width / 2}px`;
    }
}
