import { initializeVisualiser, disposeVisualiser } from "./visualiser/lifecycle.js";
import { renderVisualiser } from "./visualiser/render.js";
import { setTempoBpm, setPosition, startPlayhead, stopPlayhead, applyPlaybackState } from "./visualiser/playback.js";
import { syncNotes, addOrUpdateNote } from "./visualiser/notes.js";

const states = new WeakMap();

function getState(root) {
    return states.get(root);
}

window.visualiser = {
    _states: states,

    initialize(root, dotNetRef) {
        initializeVisualiser(states, root, dotNetRef);
    },

    setData(root, data) {
        renderVisualiser(getState(root), data);
    },

    setTempoBpm(root, tempoBpm) {
        setTempoBpm(getState(root), tempoBpm);
    },

    setPosition(root, positionSeconds, follow = true) {
        setPosition(getState(root), positionSeconds, follow);
    },

    startPlayhead(root, positionSeconds, audioAnchorTimeSeconds) {
        startPlayhead(getState(root), positionSeconds, audioAnchorTimeSeconds);
    },

    stopPlayhead(root, positionSeconds, resetViewport = false) {
        stopPlayhead(getState(root), positionSeconds, resetViewport);
    },

    syncNotes(root, notes, selectedNoteId, panLabel) {
        syncNotes(getState(root), notes, selectedNoteId, panLabel);
    },

    addOrUpdateNote(root, note, selectedNoteId, panLabel) {
        addOrUpdateNote(getState(root), note, selectedNoteId, panLabel);
    },

    setPlaybackState(root, playbackState) {
        applyPlaybackState(getState(root), playbackState);
    },

    dispose(root) {
        disposeVisualiser(states, root);
    }
};
