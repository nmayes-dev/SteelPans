import { defaults, playheadWidth } from "./constants.js";
import { getCssIsolationAttribute } from "./utils.js";
import { renderVisualiser } from "./render.js";
import { handleRecordEditKeyDown } from "./notes.js";
import { applyPlaybackState } from "./playback.js";
import { setPlayheadPosition } from "./playhead.js";
import {
    beginPlayheadDrag,
    beginViewportDrag,
    syncPlayheadAfterViewportScroll
} from "./viewport.js";

export function initializeVisualiser(states, root, dotNetRef) {
    if (!root)
        return;

    const state = createState(root, dotNetRef);
    states.set(root, state);

    root.style.setProperty(
        "--playhead-width",
        `${playheadWidth}px`);

    attachResizeObserver(state);
    attachEvents(state);

    applyPlaybackState(
        state,
        window.panPlayback?.getMidiPlaybackState?.());
}

export function disposeVisualiser(states, root) {
    const state = states.get(root);
    if (!state)
        return;

    cancelFrame(state.animationFrame);
    cancelFrame(state.dragScrollAnimationFrame);
    cancelFrame(state.resizeAnimationFrame);

    state.resizeObserver?.disconnect();

    state.ruler?.removeEventListener(
        "pointerdown",
        state.viewportPointerDown);

    state.tracks?.removeEventListener(
        "pointerdown",
        state.viewportPointerDown);

    state.viewport?.removeEventListener(
        "scroll",
        state.viewportScroll);

    state.playhead?.removeEventListener(
        "pointerdown",
        state.playheadPointerDown);

    state.root?.removeEventListener(
        "keydown",
        state.rootKeyDown);

    window.removeEventListener(
        "panplayback:midiplaybackstatechanged",
        state.playbackStateChanged);

    states.delete(root);
}

function createState(root, dotNetRef) {
    return {
        root,
        dotNetRef,
        viewport: root.querySelector(
            "[data-midi-track-visualiser-viewport]"),
        ruler: root.querySelector(
            "[data-midi-track-visualiser-ruler]"),
        tracks: root.querySelector(
            "[data-midi-track-visualiser-tracks]"),

        playhead: null,
        scopeAttribute: getCssIsolationAttribute(root),

        durationSeconds: 0,
        positionSeconds: 0,
        positionAnchorSeconds: 0,
        audioAnchorTime: null,
        isPlaying: false,
        midiStartAt: null,

        initialMidiBpm: 120,
        tempoBpm: 120,
        snapNoteDivision: 0,

        selectedNoteId: null,
        minSemitone: 0,
        maxSemitone: 0,

        animationFrame: 0,
        dragging: false,
        viewportDragging: false,
        dragPlayheadViewportX: null,
        dragScrollAnimationFrame: 0,
        dragLastPointerEvent: null,

        noteElements: [],
        noteElementsById: new Map(),
        activeNoteElements: new Set(),
        barLabels: [],

        pendingPlaybackSeek: false,
        mode: "Playback",
        suppressNextNoteClick: false,
        recordPlayheadDragging: false,
        suppressViewportScroll: false,

        lastData: null,
        resizeObserver: null,
        resizeAnimationFrame: 0,

        pixelsPerSecond: defaults.pixelsPerSecond,
        minTimelineWidth: defaults.minTimelineWidth,
        labelWidth: defaults.labelWidth,
        rulerHeight: defaults.rulerHeight,
        laneHeight: defaults.laneHeight,
        noteHeight: defaults.noteHeight,

        viewportPointerDown: null,
        playheadPointerDown: null,
        rootKeyDown: null,
        viewportScroll: null,
        playbackStateChanged: null
    };
}

function attachResizeObserver(state) {
    if (typeof ResizeObserver === "undefined")
        return;

    state.resizeObserver = new ResizeObserver(() => {
        if (!state.lastData)
            return;

        cancelFrame(state.resizeAnimationFrame);

        state.resizeAnimationFrame = requestAnimationFrame(() => {
            state.resizeAnimationFrame = 0;
            renderVisualiser(state, state.lastData);
            setPlayheadPosition(
                state,
                state.positionSeconds,
                true);
        });
    });

    state.resizeObserver.observe(state.root);
}

function attachEvents(state) {
    state.viewportPointerDown =
        event => beginViewportDrag(state, event);

    state.playheadPointerDown =
        event => beginPlayheadDrag(state, event);

    state.rootKeyDown =
        event => handleRecordEditKeyDown(state, event);

    state.viewportScroll =
        () => syncPlayheadAfterViewportScroll(state, true);

    state.playbackStateChanged =
        event => applyPlaybackState(state, event.detail);

    state.ruler?.addEventListener(
        "pointerdown",
        state.viewportPointerDown);

    state.tracks?.addEventListener(
        "pointerdown",
        state.viewportPointerDown);

    state.viewport?.addEventListener(
        "scroll",
        state.viewportScroll,
        { passive: true });

    state.root.addEventListener(
        "keydown",
        state.rootKeyDown);

    window.addEventListener(
        "panplayback:midiplaybackstatechanged",
        state.playbackStateChanged);
}

function cancelFrame(frame) {
    if (frame)
        cancelAnimationFrame(frame);
}
