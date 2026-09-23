import { clamp, createScopedElement, formatNoteFromSemitone, formatTime, getAudioTimeOrNull, getSecondsPerBeat } from "./utils.js";
import { fitNoteLabel, getNoteTop, getNoteWidth, getSemitoneFromPointerY } from "./layout.js";

export function handleRecordEditKeyDown(state, event) {
    if (!state || state.mode !== "Edit" || !state.selectedNoteId)
        return;

    const actions = {
        "+": () => resize(state, keyboardStepSeconds(state)),
        "=": () => resize(state, keyboardStepSeconds(state)),
        "-": () => resize(state, -keyboardStepSeconds(state)),
        "_": () => resize(state, -keyboardStepSeconds(state)),
        ArrowLeft: () => move(state, -keyboardStepSeconds(state)),
        ArrowRight: () => move(state, keyboardStepSeconds(state)),
        ArrowUp: () => changePitch(state, 1),
        ArrowDown: () => changePitch(state, -1),
        Delete: () => deleteSelected(state),
        Backspace: () => deleteSelected(state)
    };

    const action = actions[event.key];
    if (!action)
        return;

    event.preventDefault();
    event.stopPropagation();

    try {
        action();
        state.root?.focus?.({ preventScroll: true });
    } catch (error) {
        console.warn("Failed to handle record visualiser key", error);
    }
}

function keyboardStepSeconds(state) {
    return 60 / Math.max(1, Number(state.initialMidiBpm) || 120) / 4;
}

function resize(state, deltaSeconds) {
    void state.dotNetRef?.invokeMethodAsync("ResizeSelectedRecordNote", deltaSeconds);
}

function move(state, deltaSeconds) {
    void state.dotNetRef?.invokeMethodAsync("MoveSelectedRecordNote", deltaSeconds);
}

function changePitch(state, semitones) {
    void state.dotNetRef?.invokeMethodAsync("ChangeSelectedRecordNotePitch", semitones);
}

function deleteSelected(state) {
    void state.dotNetRef?.invokeMethodAsync("DeleteSelectedRecordNote");
}

export function syncNotes(state, notes, selectedNoteId, panLabel) {
    if (!state?.tracks)
        return;

    const incoming = Array.isArray(notes) ? notes : [];
    const keep = new Set();

    state.selectedNoteId = selectedNoteId ? String(selectedNoteId) : null;

    for (const note of incoming) {
        const id = note?.id ? String(note.id) : "";
        if (id)
            keep.add(id);

        addOrUpdateNoteElement(
            state,
            note,
            panLabel || state.lastData?.panLabel || "Unassigned");
    }

    for (const entry of [...state.noteElements]) {
        const id = entry.id || entry.element?.dataset?.noteId || "";
        if (!id || keep.has(id))
            continue;

        entry.element?.remove();
        state.noteElementsById.delete(id);
        state.activeNoteElements.delete(entry.element);
    }

    state.noteElements = state.noteElements.filter(entry => {
        const id = entry.id || entry.element?.dataset?.noteId || "";
        return !id || keep.has(id);
    });

    if (state.lastData)
        state.lastData.notes = incoming;

    applySelectedNote(state);
    updateActiveNotes(state, state.positionSeconds);
}

export function addOrUpdateNote(state, note, selectedNoteId, panLabel) {
    if (!state?.tracks || !note)
        return;

    state.selectedNoteId = selectedNoteId
        ? String(selectedNoteId)
        : state.selectedNoteId;

    addOrUpdateNoteElement(
        state,
        note,
        panLabel || state.lastData?.panLabel || "Unassigned");

    if (state.lastData) {
        const id = note?.id ? String(note.id) : "";
        const notes = Array.isArray(state.lastData.notes)
            ? state.lastData.notes
            : [];

        const index = id
            ? notes.findIndex(existing => String(existing?.id || "") === id)
            : -1;

        if (index >= 0)
            notes[index] = note;
        else
            notes.push(note);

        state.lastData.notes = notes;
        state.lastData.selectedNoteId =
            selectedNoteId ?? state.lastData.selectedNoteId;
    }

    applySelectedNote(state);
    updateActiveNotes(state, state.positionSeconds);
}

export function addOrUpdateNoteElement(state, note, panLabel) {
    if (!state?.tracks || !note)
        return null;

    const id = note.id ? String(note.id) : "";
    const start = Math.max(0, Number(note.startSeconds) || 0);
    const duration = Math.max(Number(note.durationSeconds) || 0, 0.001);
    const semitone = clamp(
        Number.isFinite(Number(note.semitone))
            ? Number(note.semitone)
            : state.minSemitone,
        state.minSemitone,
        state.maxSemitone);

    const noteLabel = note.note || formatNoteFromSemitone(semitone);
    const width = getNoteWidth(state, duration);
    const end = start + duration;
    const isEditMode = state.mode === "Edit";
    const isSelected = id && state.selectedNoteId === id;

    let entry = id ? state.noteElementsById.get(id) : null;
    let element = entry?.element ?? null;
    let label = element?.querySelector("span") ?? null;

    if (!element) {
        element = createNoteElement(state, isEditMode);
        label = createScopedElement(state, "span");
        element.appendChild(label);
        state.tracks.appendChild(element);

        entry = { id, element, start, end };
        state.noteElements.push(entry);

        if (id)
            state.noteElementsById.set(id, entry);
    }

    element.className =
        `midi-track-visualiser__note midi-track-visualiser__note--track` +
        `${isEditMode ? " midi-track-visualiser__note--editable" : ""}` +
        `${isSelected ? " midi-track-visualiser__note--selected" : ""}`;

    element.style.left = `${start * state.pixelsPerSecond}px`;
    element.style.top = `${getNoteTop(state, semitone)}px`;
    element.style.width = `${width}px`;
    element.dataset.startSeconds = String(start);
    element.dataset.endSeconds = String(end);
    element.dataset.noteId = id;
    element.dataset.semitone = String(semitone);
    element.title =
        `${panLabel || "Unassigned"} · ${noteLabel} · ` +
        `${formatTime(start)} - ${formatTime(end)}`;

    if (label) {
        label.textContent = noteLabel;
        fitNoteLabel(label, width, noteLabel, state.noteHeight);
    }

    entry.id = id;
    entry.start = start;
    entry.end = end;
    entry.element = element;

    return entry;
}

function createNoteElement(state, isEditMode) {
    const element = createScopedElement(
        state,
        isEditMode ? "button" : "div",
        `midi-track-visualiser__note midi-track-visualiser__note--track` +
        `${isEditMode ? " midi-track-visualiser__note--editable" : ""}`);

    if (!isEditMode)
        return element;

    element.type = "button";

    element.addEventListener("pointerdown", event => {
        const start = Number(element.dataset.startSeconds) || 0;
        const end = Number(element.dataset.endSeconds) || start;
        const duration = Math.max(0.001, end - start);
        const semitone = Number(element.dataset.semitone) || state.minSemitone;

        beginRecordNoteDrag(
            state,
            event,
            element.dataset.noteId || "",
            start,
            duration,
            semitone);
    });

    element.addEventListener("click", event => selectNoteFromClick(state, element, event));

    return element;
}

async function selectNoteFromClick(state, element, event) {
    event.preventDefault();
    event.stopPropagation();

    if (state.suppressNextNoteClick) {
        state.suppressNextNoteClick = false;
        return;
    }

    const id = element.dataset.noteId || "";
    if (!id)
        return;

    try {
        await state.dotNetRef?.invokeMethodAsync("SelectRecordNote", id);
    } catch (error) {
        console.warn("Failed to select record visualiser note", error);
    }
}

export function beginRecordNoteDrag(
    state,
    event,
    id,
    startSeconds,
    durationSeconds,
    semitoneNumber) {

    if (event.button !== undefined && event.button !== 0)
        return;

    if (!state || state.mode !== "Edit" || !state.viewport || !id)
        return;

    event.preventDefault();
    event.stopPropagation();

    const target = event.currentTarget;
    const pointerId = event.pointerId;
    const startClientX = event.clientX;
    const startClientY = event.clientY;
    const start = Math.max(0, Number(startSeconds) || 0);
    const duration = Math.max(0.001, Number(durationSeconds) || 0.001);
    const maxStart = Math.max(0, (state.durationSeconds || 0) - duration);
    const startSemitone = clamp(
        Number.isFinite(Number(semitoneNumber))
            ? Number(semitoneNumber)
            : state.minSemitone,
        state.minSemitone,
        state.maxSemitone);

    let currentStart = start;
    let currentSemitone = startSemitone;
    let moved = false;
    let lastPreviewedSemitone = startSemitone;

    const label = target.querySelector("span");

    state.selectedNoteId = id;
    target?.focus?.({ preventScroll: true });
    target?.setPointerCapture?.(pointerId);
    target?.classList.add("midi-track-visualiser__note--dragging");
    target?.classList.add("midi-track-visualiser__note--selected");
    state.root.classList.add("midi-track-visualiser--note-dragging");

    const movePointer = moveEvent => {
        moveEvent.preventDefault();

        const deltaX = moveEvent.clientX - startClientX;
        const deltaY = moveEvent.clientY - startClientY;

        if (Math.abs(deltaX) >= 3 || Math.abs(deltaY) >= 3)
            moved = true;

        currentStart = snapRecordNoteStart(
            state,
            start + deltaX / state.pixelsPerSecond,
            maxStart);

        currentSemitone = getSemitoneFromPointerY(state, moveEvent.clientY);

        target.style.left = `${currentStart * state.pixelsPerSecond}px`;
        target.style.top = `${getNoteTop(state, currentSemitone)}px`;

        if (currentSemitone === lastPreviewedSemitone)
            return;

        lastPreviewedSemitone = currentSemitone;
        previewDraggedPitch(state, target, label, currentSemitone, duration);
    };

    const releasePointer = async upEvent => {
        upEvent.preventDefault();
        upEvent.stopPropagation();

        target?.releasePointerCapture?.(pointerId);
        target?.classList.remove("midi-track-visualiser__note--dragging");
        state.root.classList.remove("midi-track-visualiser--note-dragging");

        removeWindowPointerHandlers(movePointer, releasePointer);

        const deltaSeconds = currentStart - start;
        const semitoneChanged = currentSemitone !== startSemitone;

        if (moved && (Math.abs(deltaSeconds) > 0.0001 || semitoneChanged)) {
            state.suppressNextNoteClick = true;

            try {
                await state.dotNetRef?.invokeMethodAsync(
                    "MoveRecordNoteAndPitch",
                    id,
                    deltaSeconds,
                    currentSemitone);
            } catch (error) {
                console.warn("Failed to move record visualiser note", error);
            }

            return;
        }

        try {
            await state.dotNetRef?.invokeMethodAsync("SelectRecordNote", id);
        } catch (error) {
            console.warn("Failed to select record visualiser note", error);
        }
    };

    addWindowPointerHandlers(movePointer, releasePointer);
}

function previewDraggedPitch(state, target, label, semitone, duration) {
    const noteLabel = formatNoteFromSemitone(semitone);
    target.title = target.title.replace(
        /· [A-G][#b]?-?\d+ ·/,
        `· ${noteLabel} ·`);

    if (label) {
        label.textContent = noteLabel;
        fitNoteLabel(
            label,
            target.getBoundingClientRect().width || getNoteWidth(state, duration),
            noteLabel,
            state.noteHeight);
    }

    try {
        void state.dotNetRef?.invokeMethodAsync(
            "PreviewRecordNoteSemitone",
            semitone);
    } catch (error) {
        console.warn("Failed to preview dragged record visualiser note", error);
    }
}

function snapRecordNoteStart(state, startSeconds, maxStart) {
    const value = clamp(startSeconds, 0, maxStart);
    const snapDivision = Number(state.snapNoteDivision) || 0;

    if (snapDivision <= 0)
        return value;

    const snapSeconds = getSecondsPerBeat(
        state.initialMidiBpm,
        snapDivision);

    if (!Number.isFinite(snapSeconds) || snapSeconds <= 0)
        return value;

    return clamp(
        Math.round(value / snapSeconds) * snapSeconds,
        0,
        maxStart);
}

export function applySelectedNote(state) {
    const selectedId = state.selectedNoteId
        ? String(state.selectedNoteId)
        : null;

    for (const entry of state.noteElements || []) {
        const id = entry.id || entry.element?.dataset?.noteId || "";
        entry.element?.classList.toggle(
            "midi-track-visualiser__note--selected",
            !!selectedId && id === selectedId);
    }
}

export function updateActiveNotes(state, positionSeconds) {
    if (!Array.isArray(state.noteElements) || state.noteElements.length === 0)
        return;

    if (!hasPlaybackReachedAudioStart(state)) {
        for (const element of state.activeNoteElements)
            element.classList.remove("midi-track-visualiser__note--active");

        state.activeNoteElements = new Set();
        return;
    }

    const active = new Set();
    const epsilon = 0.01;

    for (const note of state.noteElements) {
        if (positionSeconds + epsilon >= note.start &&
            positionSeconds <= note.end + epsilon) {
            active.add(note.element);
        }
    }

    for (const element of state.activeNoteElements) {
        if (!active.has(element))
            element.classList.remove("midi-track-visualiser__note--active");
    }

    for (const element of active) {
        if (!state.activeNoteElements.has(element))
            element.classList.add("midi-track-visualiser__note--active");
    }

    state.activeNoteElements = active;
}

function hasPlaybackReachedAudioStart(state) {
    if (!state.isPlaying || state.dragging || state.pendingPlaybackSeek)
        return false;

    if (state.midiStartAt === null)
        return true;

    const audioTime = getAudioTimeOrNull();
    return audioTime !== null && audioTime >= state.midiStartAt;
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
