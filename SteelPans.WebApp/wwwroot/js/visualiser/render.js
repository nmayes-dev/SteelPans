import { timelinePaddingRight } from "./constants.js";
import { createScopedElement, getSecondsPerBeat } from "./utils.js";
import { measureLayout, positionBarLabels } from "./layout.js";
import { addOrUpdateNoteElement, updateActiveNotes } from "./notes.js";
import { seekToBarline } from "./playback.js";
import { setPlayheadPosition } from "./playhead.js";
import { beginRecordPlayheadDrag } from "./viewport.js";

export function renderVisualiser(state, data) {
    if (!state?.ruler || !state.tracks || !data)
        return;

    state.lastData = data;

    const notes = Array.isArray(data.notes) ? data.notes : [];
    const mode = String(data.mode || "Playback");
    const isEditMode = mode === "Edit";
    const isRecordMode = mode === "Record";
    const isRecordNoteMode = isEditMode || isRecordMode;
    const duration = Math.max(
        Number(data.durationSeconds) || 0.01,
        0.01);

    const shouldRestoreKeyboardFocus =
        shouldRestoreFocus(state, data, isEditMode);

    applyDataState(state, data, mode, duration);
    applyLayout(state, duration);
    resetRenderedState(state);
    renderGrid(state, data, duration);
    renderNotes(state, notes, data.panLabel || "Unassigned");

    state.root
        .querySelector(".midi-track-visualiser__playhead")
        ?.remove();

    if (shouldRestoreKeyboardFocus) {
        requestAnimationFrame(() =>
            state.root?.focus?.({ preventScroll: true }));
    }

    renderPlayhead(
        state,
        isEditMode,
        isRecordMode,
        isRecordNoteMode,
        data.showPlayhead === true);
}

function shouldRestoreFocus(state, data, isEditMode) {
    if (!isEditMode || !data.selectedNoteId)
        return false;

    const activeElement = document.activeElement;
    return activeElement === state.root ||
        state.root.contains(activeElement);
}

function applyDataState(state, data, mode, duration) {
    state.mode = mode;

    state.initialMidiBpm = Math.max(
        Number(data.initialMidiBpm) ||
        Number(data.tempoBpm) ||
        120,
        1);

    state.tempoBpm = Math.max(
        Number(data.tempoBpm) ||
        state.initialMidiBpm,
        1);

    state.snapNoteDivision = Math.max(
        Number(data.snapNoteDivision) || 0,
        0);

    state.selectedNoteId = data.selectedNoteId
        ? String(data.selectedNoteId)
        : null;

    state.minSemitone = Number.isFinite(Number(data.minSemitone))
        ? Number(data.minSemitone)
        : 0;

    state.maxSemitone = Number.isFinite(Number(data.maxSemitone))
        ? Number(data.maxSemitone)
        : state.minSemitone;

    if (state.maxSemitone < state.minSemitone) {
        [state.minSemitone, state.maxSemitone] =
            [state.maxSemitone, state.minSemitone];
    }

    state.durationSeconds = duration;
}

function applyLayout(state, duration) {
    const layout = measureLayout(state, duration);

    Object.assign(state, layout);

    const contentWidth = Math.max(
        duration * state.pixelsPerSecond,
        state.minTimelineWidth);

    const timelineWidth =
        contentWidth + timelinePaddingRight;

    const style = state.root.style;
    style.setProperty("--label-width", `${state.labelWidth}px`);
    style.setProperty("--ruler-height", `${state.rulerHeight}px`);
    style.setProperty("--lane-height", `${state.laneHeight}px`);
    style.setProperty("--note-height", `${state.noteHeight}px`);
    style.setProperty("--timeline-width", `${timelineWidth}px`);

    state.ruler.style.width = `${timelineWidth}px`;
    state.tracks.style.width = `${timelineWidth}px`;
    state.tracks.style.height = `${state.laneHeight}px`;
}

function resetRenderedState(state) {
    state.ruler.replaceChildren();
    state.tracks.replaceChildren();

    state.playhead = null;
    state.noteElements = [];
    state.noteElementsById = new Map();
    state.activeNoteElements = new Set();
    state.barLabels = [];
}

function renderGrid(state, data, duration) {
    const secondsPerBeat = getSecondsPerBeat(
        data.initialMidiBpm,
        data.beatUnit);

    const beatsPerBar = Math.max(
        1,
        data.beatsPerBar || 4);

    const secondsPerBar = Math.max(
        0.01,
        secondsPerBeat * beatsPerBar);

    state.root.style.setProperty(
        "--bar-grid-size",
        `${secondsPerBar * state.pixelsPerSecond}px`);

    renderBeatLines(
        state,
        duration,
        secondsPerBeat,
        beatsPerBar);

    renderBarLines(
        state,
        duration,
        secondsPerBar);

    positionBarLabels(state);
}

function renderBeatLines(
    state,
    duration,
    secondsPerBeat,
    beatsPerBar) {

    for (
        let beat = 0, time = 0;
        time <= duration + 0.0001;
        beat++, time = beat * secondsPerBeat) {

        const beatline = createScopedElement(
            state,
            "div",
            `midi-track-visualiser__beatline` +
            `${beat % beatsPerBar === 0
                ? " midi-track-visualiser__beatline--bar"
                : ""}`);

        beatline.style.left =
            `${time * state.pixelsPerSecond}px`;

        state.tracks.appendChild(beatline);
    }
}

function renderBarLines(state, duration, secondsPerBar) {
    for (
        let i = 1, time = secondsPerBar;
        time <= duration + 0.0001;
        i++, time = i * secondsPerBar) {

        const left = time * state.pixelsPerSecond;
        const barline = createBarLine(state, i, left);
        const label = createBarLabel(state, i, time, barline);

        state.ruler.appendChild(label);
        state.tracks.appendChild(barline);
        state.barLabels.push({ element: label, left });
    }
}

function createBarLine(state, index, left) {
    const barline = createScopedElement(
        state,
        "div",
        `midi-track-visualiser__barline` +
        `${index % 4 === 0
            ? " midi-track-visualiser__barline--accent"
            : ""}`);

    barline.style.left = `${left}px`;
    return barline;
}

function createBarLabel(state, index, time, barline) {
    const label = createScopedElement(
        state,
        "button",
        "midi-track-visualiser__bar-label");

    label.type = "button";
    label.textContent = String(index);
    label.dataset.timeSeconds = String(time);

    label.addEventListener(
        "pointerdown",
        event => event.stopPropagation());

    label.addEventListener(
        "click",
        event => seekToBarline(state, event, time));

    label.addEventListener(
        "mouseenter",
        () => barline.classList.add(
            "midi-track-visualiser__barline--hover"));

    label.addEventListener(
        "mouseleave",
        () => barline.classList.remove(
            "midi-track-visualiser__barline--hover"));

    return label;
}

function renderNotes(state, notes, panLabel) {
    const row = createScopedElement(
        state,
        "div",
        "midi-track-visualiser__track-row");

    state.tracks.appendChild(row);

    for (const note of notes)
        addOrUpdateNoteElement(state, note, panLabel);
}

function renderPlayhead(
    state,
    isEditMode,
    isRecordMode,
    isRecordNoteMode,
    showPlayhead) {

    if (isRecordNoteMode && !showPlayhead) {
        state.playhead = null;
        return;
    }

    const playhead = createScopedElement(
        state,
        "div",
        "midi-track-visualiser__playhead");

    if (isEditMode) {
        playhead.addEventListener(
            "pointerdown",
            event => beginRecordPlayheadDrag(state, event));
    } else if (!isRecordMode) {
        playhead.addEventListener(
            "pointerdown",
            state.playheadPointerDown);
    }

    state.root.appendChild(playhead);
    state.playhead = playhead;

    setPlayheadPosition(
        state,
        state.positionSeconds,
        false);

    updateActiveNotes(
        state,
        state.positionSeconds);
}
