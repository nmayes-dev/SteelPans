import { playheadHalfWidth, scrollOffset } from "./constants.js";
import { clamp } from "./utils.js";
import { updateActiveNotes } from "./notes.js";

export function getMaxScrollLeft(state) {
    if (!state.viewport)
        return 0;

    return Math.max(
        0,
        state.tracks.scrollWidth - state.viewport.clientWidth);
}

export function setViewportScrollLeft(state, scrollLeft) {
    if (!state.viewport)
        return;

    state.suppressViewportScroll = true;
    state.viewport.scrollLeft = clamp(
        scrollLeft,
        0,
        getMaxScrollLeft(state));

    window.setTimeout(() => {
        state.suppressViewportScroll = false;
    }, 0);
}

export function scrollToPosition(state, seconds) {
    if (!state.viewport)
        return;

    const left = seconds * state.pixelsPerSecond;
    const targetViewportX = state.dragPlayheadViewportX ?? scrollOffset;

    setViewportScrollLeft(
        state,
        clamp(left - targetViewportX, 0, getMaxScrollLeft(state)));
}

export function setDraggedPlayheadPosition(state, event) {
    if (!state.viewport || !state.playhead)
        return;

    const rect = state.viewport.getBoundingClientRect();
    const viewportX = clamp(
        event.clientX - rect.left,
        playheadHalfWidth,
        state.viewport.clientWidth - playheadHalfWidth);

    state.dragPlayheadViewportX = viewportX;
    state.playhead.style.transform =
        `translateX(${viewportX - playheadHalfWidth}px)`;
}

export function getSecondsFromPointer(state, event) {
    const rect = state.viewport.getBoundingClientRect();
    const viewportX = clamp(
        event.clientX - rect.left,
        playheadHalfWidth,
        state.viewport.clientWidth - playheadHalfWidth);

    const contentX = state.viewport.scrollLeft + viewportX;
    const maxContentX = state.durationSeconds * state.pixelsPerSecond;

    return clamp(
        Math.min(contentX, maxContentX) / state.pixelsPerSecond,
        0,
        state.durationSeconds || 0);
}

export function setPlayheadPosition(state, seconds, follow) {
    const position = clamp(seconds, 0, state.durationSeconds || 0);
    const left = position * state.pixelsPerSecond;
    const scrollLeft = state.viewport?.scrollLeft ?? 0;
    let viewportX = left - scrollLeft;

    if (follow && state.viewport && !state.viewportDragging) {
        const targetViewportX = state.dragPlayheadViewportX ?? scrollOffset;

        if (position <= 0.0001) {
            setViewportScrollLeft(state, 0);
            viewportX = 0;
        } else if (viewportX > targetViewportX) {
            setViewportScrollLeft(state, left - targetViewportX);
            viewportX = targetViewportX;
        } else if (viewportX < playheadHalfWidth) {
            setViewportScrollLeft(
                state,
                Math.max(0, left - playheadHalfWidth));
            viewportX = playheadHalfWidth;
        }
    }

    viewportX = clamp(
        viewportX,
        0,
        (state.viewport?.clientWidth ?? 0) - playheadHalfWidth);

    if (state.playhead) {
        state.playhead.style.transform =
            `translateX(${viewportX - playheadHalfWidth}px)`;
    }

    updateActiveNotes(state, position);
}
