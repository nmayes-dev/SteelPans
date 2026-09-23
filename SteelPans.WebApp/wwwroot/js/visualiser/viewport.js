import { dragScrollEdgeRatio, dragScrollMaxPixelsPerSecond, playheadHalfWidth } from "./constants.js";
import { clamp, getAudioTimeOrNull } from "./utils.js";
import { updateActiveNotes } from "./notes.js";
import { updateAnimation } from "./playback.js";
import {
    getMaxScrollLeft,
    getSecondsFromPointer,
    setDraggedPlayheadPosition,
    setPlayheadPosition
} from "./playhead.js";

export function beginPlayheadDrag(state, event) {
    if (event.button !== undefined && event.button !== 0)
        return;

    if (!state ||
        state.mode === "Edit" ||
        state.mode === "Record" ||
        !state.viewport ||
        !state.tracks) {
        return;
    }

    event.preventDefault();

    state.dragging = true;
    state.dragLastPointerEvent = event;

    cancelAnimationFrameIfSet(state, "animationFrame");
    cancelAnimationFrameIfSet(state, "dragScrollAnimationFrame");

    const target = event.currentTarget;
    const pointerId = event.pointerId;
    target?.setPointerCapture?.(pointerId);

    const move = moveEvent => {
        state.dragLastPointerEvent = moveEvent;

        const seconds = getSecondsFromPointer(state, moveEvent);
        setPositionAnchor(state, seconds);

        setDraggedPlayheadPosition(state, moveEvent);
        updateActiveNotes(state, seconds);
        updateDragAutoScroll(state);
    };

    const up = async upEvent => {
        target?.releasePointerCapture?.(pointerId);
        removeWindowPointerHandlers(move, up);

        cancelAnimationFrameIfSet(state, "dragScrollAnimationFrame");

        const seconds = getSecondsFromPointer(state, upEvent);

        state.dragging = false;
        state.dragLastPointerEvent = null;
        setPositionAnchor(state, seconds);

        setDraggedPlayheadPosition(state, upEvent);
        updateActiveNotes(state, seconds);

        const wasPlaying = state.isPlaying;

        if (wasPlaying) {
            state.pendingPlaybackSeek = true;
            state.isPlaying = false;
        }

        try {
            await state.dotNetRef?.invokeMethodAsync(
                "PreviewSeekSeconds",
                seconds);

            await state.dotNetRef?.invokeMethodAsync(
                "CommitSeekSeconds",
                seconds);
        } catch (error) {
            console.warn("Failed to commit MIDI visualiser seek", error);
        }

        if (!wasPlaying)
            updateAnimation(state);
    };

    move(event);
    addWindowPointerHandlers(move, up);
}

export function beginViewportDrag(state, event) {
    if (event.button !== undefined && event.button !== 0)
        return;

    if (!state ||
        !state.viewport ||
        !state.tracks ||
        state.mode === "Record") {
        return;
    }

    event.preventDefault();

    const target = event.currentTarget;
    const pointerId = event.pointerId;
    const startClientX = event.clientX;
    const startScrollLeft = state.viewport.scrollLeft;

    state.viewportDragging = true;
    state.root.classList.add(
        "midi-track-visualiser--viewport-dragging");

    target?.setPointerCapture?.(pointerId);

    const move = moveEvent => {
        const deltaX = moveEvent.clientX - startClientX;

        state.viewport.scrollLeft = clamp(
            startScrollLeft - deltaX,
            0,
            getMaxScrollLeft(state));

        syncPlayheadAfterViewportScroll(state, true);
    };

    const up = () => {
        target?.releasePointerCapture?.(pointerId);
        removeWindowPointerHandlers(move, up);

        state.viewportDragging = false;
        state.root.classList.remove(
            "midi-track-visualiser--viewport-dragging");

        syncPlayheadAfterViewportScroll(state, true);
    };

    addWindowPointerHandlers(move, up);
}

export function beginRecordPlayheadDrag(state, event) {
    if (event.button !== undefined && event.button !== 0)
        return;

    if (!state ||
        state.mode !== "Edit" ||
        !state.viewport ||
        !state.tracks) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();

    const target = event.currentTarget;
    const pointerId = event.pointerId;

    state.recordPlayheadDragging = true;
    state.root?.focus?.({ preventScroll: true });
    target?.setPointerCapture?.(pointerId);

    const move = moveEvent => {
        moveEvent.preventDefault();

        const seconds = getSecondsFromPointer(state, moveEvent);
        setPositionAnchor(state, seconds);

        setDraggedPlayheadPosition(state, moveEvent);
        updateActiveNotes(state, seconds);
    };

    const up = async upEvent => {
        upEvent.preventDefault();
        upEvent.stopPropagation();

        target?.releasePointerCapture?.(pointerId);
        removeWindowPointerHandlers(move, up);

        state.recordPlayheadDragging = false;

        const seconds = getSecondsFromPointer(state, upEvent);
        setPositionAnchor(state, seconds);
        setPlayheadPosition(state, seconds, false);

        try {
            await state.dotNetRef?.invokeMethodAsync(
                "SetRecordPositionSeconds",
                seconds);
        } catch (error) {
            console.warn("Failed to set record visualiser position", error);
        }
    };

    move(event);
    addWindowPointerHandlers(move, up);
}

export function syncPlayheadAfterViewportScroll(state, commit) {
    if (!state ||
        !state.viewport ||
        !state.playhead ||
        state.suppressViewportScroll ||
        state.recordPlayheadDragging ||
        state.dragging ||
        state.mode === "Record") {
        return;
    }

    const viewportLeft = state.viewport.scrollLeft;
    const viewportRight = viewportLeft + state.viewport.clientWidth;
    const playheadContentX =
        state.positionSeconds * state.pixelsPerSecond;

    let nextPosition = null;
    let nextViewportX = null;

    if (playheadContentX < viewportLeft) {
        nextPosition = viewportLeft / state.pixelsPerSecond;
        nextViewportX = playheadHalfWidth;
    } else if (playheadContentX > viewportRight - playheadHalfWidth) {
        nextPosition =
            (viewportRight - playheadHalfWidth) /
            state.pixelsPerSecond;

        nextViewportX =
            state.viewport.clientWidth - playheadHalfWidth;
    }

    if (nextPosition === null) {
        setPlayheadPosition(
            state,
            state.positionSeconds,
            false);
        return;
    }

    const seconds = clamp(
        nextPosition,
        0,
        state.durationSeconds || 0);

    setPositionAnchor(state, seconds);

    state.dragPlayheadViewportX = nextViewportX;
    state.playhead.style.transform =
        `translateX(${nextViewportX - playheadHalfWidth}px)`;

    updateActiveNotes(state, seconds);

    if (commit)
        void commitPositionChangedByViewportScroll(state, seconds);
}

async function commitPositionChangedByViewportScroll(state, seconds) {
    try {
        if (state.mode === "Edit" || state.mode === "Record") {
            await state.dotNetRef?.invokeMethodAsync(
                "SetRecordPositionSeconds",
                seconds);
        } else if (!state.isPlaying) {
            await state.dotNetRef?.invokeMethodAsync(
                "PreviewSeekSeconds",
                seconds);

            await state.dotNetRef?.invokeMethodAsync(
                "CommitSeekSeconds",
                seconds);
        }
    } catch (error) {
        console.warn(
            "Failed to sync visualiser playhead after viewport scroll",
            error);
    }
}

function updateDragAutoScroll(state) {
    if (!state.dragging ||
        !state.viewport ||
        !state.dragLastPointerEvent ||
        state.dragScrollAnimationFrame) {
        return;
    }

    let lastTimestamp = null;

    const tick = timestamp => {
        if (!state.dragging ||
            !state.viewport ||
            !state.dragLastPointerEvent) {
            state.dragScrollAnimationFrame = 0;
            return;
        }

        const rect = state.viewport.getBoundingClientRect();
        const viewportWidth = state.viewport.clientWidth;
        const edgeWidth = Math.max(
            24,
            viewportWidth * dragScrollEdgeRatio);

        const pointerX =
            state.dragLastPointerEvent.clientX - rect.left;

        const { direction, intensity } =
            getAutoScrollDirection(pointerX, viewportWidth, edgeWidth);

        if (direction !== 0) {
            applyAutoScroll(
                state,
                rect,
                direction,
                intensity,
                lastTimestamp,
                timestamp);
        }

        lastTimestamp = timestamp;
        state.dragScrollAnimationFrame =
            requestAnimationFrame(tick);
    };

    state.dragScrollAnimationFrame = requestAnimationFrame(tick);
}

function getAutoScrollDirection(pointerX, viewportWidth, edgeWidth) {
    if (pointerX >= viewportWidth - edgeWidth) {
        return {
            direction: 1,
            intensity: clamp(
                (pointerX - (viewportWidth - edgeWidth)) / edgeWidth,
                0,
                1)
        };
    }

    if (pointerX <= edgeWidth) {
        return {
            direction: -1,
            intensity: clamp(
                (edgeWidth - pointerX) / edgeWidth,
                0,
                1)
        };
    }

    return { direction: 0, intensity: 0 };
}

function applyAutoScroll(
    state,
    rect,
    direction,
    intensity,
    lastTimestamp,
    timestamp) {

    const deltaSeconds = lastTimestamp === null
        ? 0
        : Math.max(0, (timestamp - lastTimestamp) / 1000);

    const deltaPixels =
        direction *
        dragScrollMaxPixelsPerSecond *
        intensity *
        deltaSeconds;

    state.viewport.scrollLeft = clamp(
        state.viewport.scrollLeft + deltaPixels,
        0,
        getMaxScrollLeft(state));

    const pointerViewportX = clamp(
        state.dragLastPointerEvent.clientX - rect.left,
        playheadHalfWidth,
        state.viewport.clientWidth - playheadHalfWidth);

    const contentX =
        state.viewport.scrollLeft + pointerViewportX;

    const maxContentX =
        state.durationSeconds * state.pixelsPerSecond;

    const seconds = clamp(
        Math.min(contentX, maxContentX) /
        state.pixelsPerSecond,
        0,
        state.durationSeconds || 0);

    setPositionAnchor(state, seconds);
    state.dragPlayheadViewportX = pointerViewportX;

    if (state.playhead) {
        state.playhead.style.transform =
            `translateX(${pointerViewportX - playheadHalfWidth}px)`;
    }

    updateActiveNotes(state, seconds);
}

function setPositionAnchor(state, seconds) {
    state.positionSeconds = seconds;
    state.positionAnchorSeconds = seconds;
    state.audioAnchorTime = getAudioTimeOrNull();
}

function cancelAnimationFrameIfSet(state, key) {
    if (!state[key])
        return;

    cancelAnimationFrame(state[key]);
    state[key] = 0;
}

function addWindowPointerHandlers(move, up) {
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
}

function removeWindowPointerHandlers(move, up) {
    window.removeEventListener("pointermove", move);
    window.removeEventListener("pointerup", up);
    window.removeEventListener("pointercancel", up);
}
