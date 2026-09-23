import { clamp, getAudioTimeOrNull } from "./utils.js";
import { scrollToPosition, setPlayheadPosition, setViewportScrollLeft } from "./playhead.js";

export function setTempoBpm(state, tempoBpm) {
    if (!state)
        return;

    const nextTempoBpm = Math.max(
        Number(tempoBpm) ||
        state.tempoBpm ||
        state.initialMidiBpm ||
        120,
        1);

    if (Math.abs(nextTempoBpm - state.tempoBpm) < 0.0001)
        return;

    const audioTime = getAudioTimeOrNull();

    if (state.isPlaying &&
        audioTime !== null &&
        state.audioAnchorTime !== null) {

        const elapsed = Math.max(0, audioTime - state.audioAnchorTime);
        const previousTempoRatio =
            state.tempoBpm / Math.max(state.initialMidiBpm, 1);

        state.positionSeconds = clamp(
            state.positionAnchorSeconds + elapsed * previousTempoRatio,
            0,
            state.durationSeconds);

        state.positionAnchorSeconds = state.positionSeconds;
        state.audioAnchorTime = audioTime;

        setPlayheadPosition(state, state.positionSeconds, true);
    }

    state.tempoBpm = nextTempoBpm;

    if (state.lastData)
        state.lastData.tempoBpm = nextTempoBpm;

    updateAnimation(state);
}

export function setPosition(state, positionSeconds, follow = true) {
    if (!state)
        return;

    const position = clamp(
        Number(positionSeconds) || 0,
        0,
        state.durationSeconds || 0);

    state.positionSeconds = position;
    state.positionAnchorSeconds = position;
    state.audioAnchorTime = getAudioTimeOrNull();

    if (position <= 0.0001) {
        state.dragPlayheadViewportX = null;
        setViewportScrollLeft(state, 0);
    }

    setPlayheadPosition(state, position, follow === true);
}

export function startPlayhead(
    state,
    positionSeconds,
    audioAnchorTimeSeconds) {

    if (!state)
        return;

    const position = clamp(
        Number(positionSeconds) || 0,
        0,
        state.durationSeconds || 0);

    const audioAnchor = Number(audioAnchorTimeSeconds);

    state.isPlaying = true;
    state.pendingPlaybackSeek = false;
    state.positionSeconds = position;
    state.positionAnchorSeconds = position;
    state.audioAnchorTime = Number.isFinite(audioAnchor)
        ? audioAnchor
        : getAudioTimeOrNull();

    setPlayheadPosition(state, position, true);
    updateAnimation(state);
}

export function stopPlayhead(
    state,
    positionSeconds,
    resetViewport = false) {

    if (!state)
        return;

    const position = clamp(
        Number(positionSeconds) || 0,
        0,
        state.durationSeconds || 0);

    state.isPlaying = false;
    state.pendingPlaybackSeek = false;
    state.positionSeconds = position;
    state.positionAnchorSeconds = position;
    state.audioAnchorTime = getAudioTimeOrNull();

    cancelAnimation(state);

    if (resetViewport) {
        state.dragPlayheadViewportX = null;
        setViewportScrollLeft(state, 0);
    }

    setPlayheadPosition(state, position, false);
}

export function applyPlaybackState(state, playbackState) {
    if (!state || !playbackState)
        return;

    const playbackDuration =
        Number(playbackState.durationSeconds) ||
        state.durationSeconds ||
        0.01;

    const duration = state.lastData
        ? Math.max(
            state.durationSeconds ||
            Number(state.lastData.durationSeconds) ||
            0.01,
            0.01)
        : Math.max(playbackDuration, 0.01);

    const position = clamp(
        Number(playbackState.positionSeconds) || 0,
        0,
        duration);

    const midiStartAt = Number(playbackState.midiStartAt);
    const audioAnchorTime = Number(playbackState.audioAnchorTime);

    state.isPlaying = playbackState.isPlaying === true;
    state.positionSeconds = position;
    state.positionAnchorSeconds = position;
    state.durationSeconds = duration;
    state.midiStartAt = Number.isFinite(midiStartAt)
        ? midiStartAt
        : null;

    if (!state.lastData) {
        state.initialMidiBpm = Math.max(
            Number(playbackState.initialMidiBpm) || 120,
            1);
    }

    state.tempoBpm = Math.max(
        Number(playbackState.tempoBpm) ||
        state.tempoBpm ||
        state.initialMidiBpm,
        1);

    state.pendingPlaybackSeek = false;

    state.audioAnchorTime = state.isPlaying
        ? Number.isFinite(audioAnchorTime)
            ? audioAnchorTime
            : state.midiStartAt ?? getAudioTimeOrNull()
        : getAudioTimeOrNull();

    scrollToPosition(state, state.positionSeconds);
    setPlayheadPosition(state, state.positionSeconds, false);
    updateAnimation(state);
}

export function updateAnimation(state) {
    cancelAnimation(state);

    if (!state?.isPlaying ||
        state.dragging ||
        state.pendingPlaybackSeek) {
        return;
    }

    if (state.audioAnchorTime === null)
        state.audioAnchorTime = getAudioTimeOrNull();

    const tick = () => {
        if (!state.isPlaying ||
            state.dragging ||
            state.pendingPlaybackSeek) {
            state.animationFrame = 0;
            return;
        }

        const audioTime = getAudioTimeOrNull();

        if (audioTime !== null && state.audioAnchorTime !== null) {
            const elapsed = Math.max(
                0,
                audioTime - state.audioAnchorTime);

            const tempoRatio =
                state.tempoBpm / Math.max(state.initialMidiBpm, 1);

            const position = clamp(
                state.positionAnchorSeconds + elapsed * tempoRatio,
                0,
                state.durationSeconds);

            state.positionSeconds = position;
            setPlayheadPosition(state, position, true);

            if (position >= state.durationSeconds) {
                state.animationFrame = 0;
                return;
            }
        }

        state.animationFrame = requestAnimationFrame(tick);
    };

    state.animationFrame = requestAnimationFrame(tick);
}

export async function seekToBarline(state, event, seconds) {
    event.preventDefault();
    event.stopPropagation();

    const position = clamp(
        Number(seconds) || 0,
        0,
        state.durationSeconds || 0);

    state.positionSeconds = position;
    state.positionAnchorSeconds = position;
    state.audioAnchorTime = getAudioTimeOrNull();

    setPlayheadPosition(state, position, true);

    const wasPlaying = state.isPlaying;

    if (wasPlaying) {
        state.pendingPlaybackSeek = true;
        state.isPlaying = false;
    }

    try {
        if (state.mode === "Edit" || state.mode === "Record") {
            await state.dotNetRef?.invokeMethodAsync(
                "SetRecordPositionSeconds",
                position);
        } else {
            await state.dotNetRef?.invokeMethodAsync(
                "PreviewSeekSeconds",
                position);

            await state.dotNetRef?.invokeMethodAsync(
                "CommitSeekSeconds",
                position);
        }
    } catch (error) {
        console.warn("Failed to commit MIDI visualiser barline seek", error);
    }

    if (!wasPlaying)
        updateAnimation(state);
}

function cancelAnimation(state) {
    if (!state?.animationFrame)
        return;

    cancelAnimationFrame(state.animationFrame);
    state.animationFrame = 0;
}
