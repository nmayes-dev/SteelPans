const defaultPixelsPerSecond = 192;
const defaultMinTimelineWidth = 960;
const defaultLabelWidth = 200;
const defaultRulerHeight = 24;
const defaultLaneHeight = 160;
const defaultNoteHeight = 10;
const playheadWidth = 3;
const timelinePaddingRight = playheadWidth;
const playheadHalfWidth = playheadWidth / 2;
const minNoteLabelWidth = 24;
const scrollOffset = 32;
const dragScrollEdgeRatio = 0.25;
const dragScrollMaxPixelsPerSecond = 1920;

window.visualiser = {
    _states: new WeakMap(),

    initialize(root, dotNetRef) {
        if (!root)
            return;

        const viewport = root.querySelector("[data-midi-track-visualiser-viewport]");
        const ruler = root.querySelector("[data-midi-track-visualiser-ruler]");
        const tracks = root.querySelector("[data-midi-track-visualiser-tracks]");

        const state = {
            root,
            dotNetRef,
            viewport,
            ruler,
            tracks,
            playhead: null,
            scopeAttribute: this._getCssIsolationAttribute(root),
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
            activeNoteElements: new Set(),
            barLabels: [],
            pendingPlaybackSeek: false,
            playbackStateChanged: null,
            mode: "Playback",
            suppressNextNoteClick: false,
            recordPlayheadDragging: false,
            suppressViewportScroll: false,
            viewportScroll: null,
            lastPreviewedDragSemitone: null,
            lastData: null,
            resizeObserver: null,
            resizeAnimationFrame: 0,
            pixelsPerSecond: defaultPixelsPerSecond,
            minTimelineWidth: defaultMinTimelineWidth,
            labelWidth: defaultLabelWidth,
            rulerHeight: defaultRulerHeight,
            laneHeight: defaultLaneHeight,
            noteHeight: defaultNoteHeight,
        };

        this._states.set(root, state);

        root.style.setProperty("--playhead-width", `${playheadWidth}px`);

        if (typeof ResizeObserver !== "undefined") {
            state.resizeObserver = new ResizeObserver(() => {
                if (!state.lastData)
                    return;

                if (state.resizeAnimationFrame)
                    cancelAnimationFrame(state.resizeAnimationFrame);

                state.resizeAnimationFrame = requestAnimationFrame(() => {
                    state.resizeAnimationFrame = 0;
                    this.setData(root, state.lastData);
                    this._setPlayheadPosition(state, state.positionSeconds, true);
                });
            });
            state.resizeObserver.observe(root);
        }

        const viewportPointerDown = event => this._beginViewportDrag(root, event);
        const playheadPointerDown = event => this._beginPlayheadDrag(root, event);
        const rootKeyDown = event => this._handleRecordEditKeyDown(state, event);
        const viewportScroll = () => this._syncPlayheadAfterViewportScroll(state, true);
        const playbackStateChanged = event => this._applyPlaybackState(state, event.detail);

        ruler?.addEventListener("pointerdown", viewportPointerDown);
        tracks?.addEventListener("pointerdown", viewportPointerDown);
        viewport?.addEventListener("scroll", viewportScroll, { passive: true });
        root.addEventListener("keydown", rootKeyDown);
        window.addEventListener("panplayback:midiplaybackstatechanged", playbackStateChanged);

        state.viewportPointerDown = viewportPointerDown;
        state.playheadPointerDown = playheadPointerDown;
        state.rootKeyDown = rootKeyDown;
        state.viewportScroll = viewportScroll;
        state.playbackStateChanged = playbackStateChanged;

        this._applyPlaybackState(state, window.panPlayback?.getMidiPlaybackState?.());
    },

    setData(root, data) {
        const state = this._states.get(root);
        if (!state || !state.ruler || !state.tracks || !data)
            return;

        state.lastData = data;

        const notes = Array.isArray(data.notes) ? data.notes : [];
        const mode = String(data.mode || "Playback");
        const isEditMode = mode === "Edit";
        const isRecordMode = mode === "Record";
        const isRecordNoteMode = isEditMode || isRecordMode;
        const durationSeconds = Math.max(Number(data.durationSeconds) || 0.01, 0.01);
        const activeElement = document.activeElement;
        const shouldRestoreKeyboardFocus = isEditMode
            && data.selectedNoteId
            && (activeElement === state.root || state.root.contains(activeElement));

        state.mode = mode;
        state.initialMidiBpm = Math.max(Number(data.initialMidiBpm) || Number(data.tempoBpm) || 120, 1);
        state.tempoBpm = Math.max(Number(data.tempoBpm) || state.initialMidiBpm, 1);
        state.snapNoteDivision = Math.max(Number(data.snapNoteDivision) || 0, 0);
        state.selectedNoteId = data.selectedNoteId ? String(data.selectedNoteId) : null;
        state.minSemitone = Number.isFinite(Number(data.minSemitone)) ? Number(data.minSemitone) : 0;
        state.maxSemitone = Number.isFinite(Number(data.maxSemitone)) ? Number(data.maxSemitone) : state.minSemitone;
        if (state.maxSemitone < state.minSemitone) {
            const temp = state.minSemitone;
            state.minSemitone = state.maxSemitone;
            state.maxSemitone = temp;
        }
        const layout = this._measureLayout(state, durationSeconds);
        state.pixelsPerSecond = layout.pixelsPerSecond;
        state.minTimelineWidth = layout.minTimelineWidth;
        state.labelWidth = layout.labelWidth;
        state.rulerHeight = layout.rulerHeight;
        state.laneHeight = layout.laneHeight;
        state.noteHeight = layout.noteHeight;

        const contentWidth = Math.max(durationSeconds * state.pixelsPerSecond, state.minTimelineWidth);
        const timelineWidth = contentWidth + timelinePaddingRight;

        state.durationSeconds = durationSeconds;
        root.style.setProperty("--label-width", `${state.labelWidth}px`);
        root.style.setProperty("--ruler-height", `${state.rulerHeight}px`);
        root.style.setProperty("--lane-height", `${state.laneHeight}px`);
        root.style.setProperty("--note-height", `${state.noteHeight}px`);
        root.style.setProperty("--timeline-width", `${timelineWidth}px`);

        state.ruler.style.width = `${timelineWidth}px`;
        state.tracks.style.width = `${timelineWidth}px`;
        state.tracks.style.height = `${state.laneHeight}px`;

        state.ruler.replaceChildren();
        state.tracks.replaceChildren();
        state.playhead = null;
        state.noteElements = [];
        state.activeNoteElements = new Set();
        state.barLabels = [];

        const secondsPerBeat = this._getSecondsPerBeat(data.tempoBpm, data.beatUnit);
        const beatsPerBar = Math.max(1, data.beatsPerBar || 4);
        const secondsPerBar = Math.max(0.01, secondsPerBeat * beatsPerBar);
        root.style.setProperty("--bar-grid-size", `${secondsPerBar * state.pixelsPerSecond}px`);

        for (let beat = 0, time = 0; time <= durationSeconds + 0.0001; beat++, time = beat * secondsPerBeat) {
            const left = time * state.pixelsPerSecond;
            const beatline = this._createScopedElement(
                state,
                "div",
                `midi-track-visualiser__beatline${beat % beatsPerBar === 0 ? " midi-track-visualiser__beatline--bar" : ""}`);
            beatline.style.left = `${left}px`;
            state.tracks.appendChild(beatline);
        }

        for (let i = 1, time = secondsPerBar; time <= durationSeconds + 0.0001; i++, time = i * secondsPerBar) {
            const left = time * state.pixelsPerSecond;

            const label = this._createScopedElement(state, "button", "midi-track-visualiser__bar-label");
            label.type = "button";
            label.textContent = String(i);
            label.dataset.timeSeconds = String(time);
            label.addEventListener("pointerdown", event => event.stopPropagation());
            label.addEventListener("click", event => this._seekToBarline(state, event, time));

            const barline = this._createScopedElement(
                state,
                "div",
                `midi-track-visualiser__barline${i % 4 === 0 ? " midi-track-visualiser__barline--accent" : ""}`);
            barline.style.left = `${left}px`;

            label.addEventListener("mouseenter", () => barline.classList.add("midi-track-visualiser__barline--hover"));
            label.addEventListener("mouseleave", () => barline.classList.remove("midi-track-visualiser__barline--hover"));

            state.ruler.appendChild(label);
            state.barLabels.push({ element: label, left });
            state.tracks.appendChild(barline);
        }

        this._positionBarLabels(state);

        const row = this._createScopedElement(state, "div", "midi-track-visualiser__track-row");
        state.tracks.appendChild(row);

        const minSemitone = state.minSemitone;
        const maxSemitone = state.maxSemitone;
        const range = Math.max(1, maxSemitone - minSemitone);
        const availableHeight = state.laneHeight - state.noteHeight - 22;

        for (const note of notes) {
            const start = Math.max(0, Number(note.startSeconds) || 0);
            const duration = Math.max(Number(note.durationSeconds) || 0, 0.001);
            const semitone = this._clamp(
                Number.isFinite(Number(note.semitone)) ? Number(note.semitone) : minSemitone,
                minSemitone,
                maxSemitone);
            const normalized = (semitone - minSemitone) / range;
            const top = 11 + ((1.0 - normalized) * availableHeight);

            const id = note.id ? String(note.id) : "";
            const isSelected = note.isSelected === true;
            const element = this._createScopedElement(
                state,
                isEditMode ? "button" : "div",
                `midi-track-visualiser__note midi-track-visualiser__note--track${isEditMode ? " midi-track-visualiser__note--editable" : ""}${isSelected ? " midi-track-visualiser__note--selected" : ""}`);

            if (isEditMode) {
                element.type = "button";
                element.addEventListener("pointerdown", event => this._beginRecordNoteDrag(state, event, id, start, duration, semitone));
                element.addEventListener("click", async event => {
                    event.preventDefault();
                    event.stopPropagation();

                    if (state.suppressNextNoteClick) {
                        state.suppressNextNoteClick = false;
                        return;
                    }

                    if (!id)
                        return;

                    try {
                        await state.dotNetRef?.invokeMethodAsync("SelectRecordNote", id);
                    } catch (error) {
                        console.warn("Failed to select record visualiser note", error);
                    }
                });
            }
            const width = this._getNoteWidth(state, duration, note.note || "");
            const end = start + duration;

            element.style.left = `${start * state.pixelsPerSecond}px`;
            element.style.top = `${top}px`;
            element.style.width = `${width}px`;
            element.dataset.startSeconds = String(start);
            element.dataset.endSeconds = String(end);
            element.dataset.noteId = id;
            element.title = `${data.panLabel || "Unassigned"} · ${note.note || "Note"} · ${this._formatTime(start)} - ${this._formatTime(end)}`;

            const label = this._createScopedElement(state, "span");
            label.textContent = note.note || "";
            this._fitNoteLabel(label, width, note.note || "");
            element.appendChild(label);

            state.noteElements.push({ element, start, end });
            state.tracks.appendChild(element);
        }

        state.root.querySelector(".midi-track-visualiser__playhead")?.remove();

        if (shouldRestoreKeyboardFocus)
            requestAnimationFrame(() => state.root?.focus?.({ preventScroll: true }));

        if (!isRecordNoteMode || data.showPlayhead === true) {
            const playhead = this._createScopedElement(state, "div", "midi-track-visualiser__playhead");

            if (isEditMode)
                playhead.addEventListener("pointerdown", event => this._beginRecordPlayheadDrag(state, event));
            else if (!isRecordMode)
                playhead.addEventListener("pointerdown", state.playheadPointerDown);

            state.root.appendChild(playhead);
            state.playhead = playhead;

            this._setPlayheadPosition(state, state.positionSeconds, false);
            this._updateActiveNotes(state, state.positionSeconds);
        } else {
            state.playhead = null;
        }
    },

    setPosition(root, positionSeconds) {
        const state = this._states.get(root);
        if (!state)
            return;

        const position = this._clamp(Number(positionSeconds) || 0, 0, state.durationSeconds || 0);
        state.positionSeconds = position;
        state.positionAnchorSeconds = position;
        state.audioAnchorTime = this._getAudioTimeOrNull();

        if (position <= 0.0001) {
            state.dragPlayheadViewportX = null;
            this._setViewportScrollLeft(state, 0);
        }

        this._setPlayheadPosition(state, position, true);
    },

    setPlaybackState(root, playbackState) {
        const state = this._states.get(root);
        this._applyPlaybackState(state, playbackState);
    },

    _applyPlaybackState(state, playbackState) {
        if (!state || !playbackState)
            return;

        const durationSeconds = Number(playbackState.durationSeconds) || state.durationSeconds || 0.01;
        const positionSeconds = this._clamp(Number(playbackState.positionSeconds) || 0, 0, durationSeconds);
        const midiStartAt = Number(playbackState.midiStartAt);
        const audioAnchorTime = Number(playbackState.audioAnchorTime);

        state.isPlaying = playbackState.isPlaying === true;
        state.positionSeconds = positionSeconds;
        state.positionAnchorSeconds = positionSeconds;
        state.durationSeconds = Math.max(durationSeconds, 0.01);
        state.midiStartAt = Number.isFinite(midiStartAt) ? midiStartAt : null;
        state.initialMidiBpm = Math.max(Number(playbackState.initialMidiBpm) || 120, 1);
        state.tempoBpm = Math.max(Number(playbackState.tempoBpm) || state.initialMidiBpm, 1);
        state.pendingPlaybackSeek = false;

        if (state.isPlaying) {
            state.audioAnchorTime = Number.isFinite(audioAnchorTime)
                ? audioAnchorTime
                : state.midiStartAt ?? this._getAudioTimeOrNull();
        } else {
            state.audioAnchorTime = this._getAudioTimeOrNull();
        }

        this._scrollToPosition(state, state.positionSeconds);
        this._setPlayheadPosition(state, state.positionSeconds, false);

        this._updateAnimation(state);
    },

    dispose(root) {
        const state = this._states.get(root);
        if (!state)
            return;

        if (state.animationFrame)
            cancelAnimationFrame(state.animationFrame);

        if (state.dragScrollAnimationFrame)
            cancelAnimationFrame(state.dragScrollAnimationFrame);

        if (state.resizeAnimationFrame)
            cancelAnimationFrame(state.resizeAnimationFrame);

        state.resizeObserver?.disconnect();

        state.ruler?.removeEventListener("pointerdown", state.viewportPointerDown);
        state.tracks?.removeEventListener("pointerdown", state.viewportPointerDown);
        state.viewport?.removeEventListener("scroll", state.viewportScroll);
        state.playhead?.removeEventListener("pointerdown", state.playheadPointerDown);

        if (state.rootKeyDown)
            state.root?.removeEventListener("keydown", state.rootKeyDown);

        if (state.playbackStateChanged)
            window.removeEventListener("panplayback:midiplaybackstatechanged", state.playbackStateChanged);

        this._states.delete(root);
    },

    _measureLayout(state, durationSeconds) {
        const rootWidth = Math.max(280, state.root?.getBoundingClientRect?.().width || 0);
        const viewportWidth = Math.max(180, rootWidth - Math.min(defaultLabelWidth, rootWidth * 0.36));
        const compactness = this._clamp((rootWidth - 320) / 720, 0, 1);

        return {
            labelWidth: Math.round(this._clamp(rootWidth * 0.2, 96, 200)),
            rulerHeight: Math.round(22 + (compactness * 6)),
            laneHeight: Math.round(this._clamp(rootWidth * 0.15, 100, 220)),
            noteHeight: Math.round(this._clamp(rootWidth * 0.01, 6, 14)),
            pixelsPerSecond: this._clamp(viewportWidth / Math.max(4, Math.min(durationSeconds, 8)), 112, defaultPixelsPerSecond),
            minTimelineWidth: Math.max(Math.round(viewportWidth), 560)
        };
    },

    _handleRecordEditKeyDown(state, event) {
        if (!state || state.mode !== "Edit" || !state.selectedNoteId)
            return;

        const key = event.key;
        const handled = key === "+"
            || key === "="
            || key === "-"
            || key === "_"
            || key === "ArrowLeft"
            || key === "ArrowRight"
            || key === "ArrowUp"
            || key === "ArrowDown"
            || key === "Delete"
            || key === "Backspace";

        if (!handled)
            return;

        event.preventDefault();
        event.stopPropagation();

        try {
            if (key === "+" || key === "=") {
                void state.dotNetRef?.invokeMethodAsync("ResizeSelectedRecordNote", this._getKeyboardStepSeconds(state));
            } else if (key === "-" || key === "_") {
                void state.dotNetRef?.invokeMethodAsync("ResizeSelectedRecordNote", -this._getKeyboardStepSeconds(state));
            } else if (key === "ArrowLeft") {
                void state.dotNetRef?.invokeMethodAsync("MoveSelectedRecordNote", -this._getKeyboardStepSeconds(state));
            } else if (key === "ArrowRight") {
                void state.dotNetRef?.invokeMethodAsync("MoveSelectedRecordNote", this._getKeyboardStepSeconds(state));
            } else if (key === "ArrowUp") {
                void state.dotNetRef?.invokeMethodAsync("ChangeSelectedRecordNotePitch", 1);
            } else if (key === "ArrowDown") {
                void state.dotNetRef?.invokeMethodAsync("ChangeSelectedRecordNotePitch", -1);
            } else if (key === "Delete" || key === "Backspace") {
                void state.dotNetRef?.invokeMethodAsync("DeleteSelectedRecordNote");
            }

            state.root?.focus?.({ preventScroll: true });
        } catch (error) {
            console.warn("Failed to handle record visualiser key", error);
        }
    },

    _getKeyboardStepSeconds(state) {
        return 60.0 / Math.max(1, Number(state.tempoBpm) || 120) / 4.0;
    },

    _beginPlayheadDrag(root, event) {
        if (event.button !== undefined && event.button !== 0)
            return;

        const state = this._states.get(root);
        if (!state || (state.mode === "Edit" || state.mode === "Record") || !state.viewport || !state.tracks)
            return;

        event.preventDefault();
        state.dragging = true;
        state.dragLastPointerEvent = event;

        if (state.animationFrame) {
            cancelAnimationFrame(state.animationFrame);
            state.animationFrame = 0;
        }

        if (state.dragScrollAnimationFrame) {
            cancelAnimationFrame(state.dragScrollAnimationFrame);
            state.dragScrollAnimationFrame = 0;
        }

        const pointerId = event.pointerId;
        const target = event.currentTarget;
        target?.setPointerCapture?.(pointerId);

        const move = moveEvent => {
            state.dragLastPointerEvent = moveEvent;

            const seconds = this._getSecondsFromPointer(state, moveEvent);
            state.positionSeconds = seconds;
            state.positionAnchorSeconds = seconds;
            state.audioAnchorTime = this._getAudioTimeOrNull();

            this._setDraggedPlayheadPosition(state, moveEvent);
            this._updateActiveNotes(state, seconds);
            this._updateDragAutoScroll(state);
        };

        const up = async upEvent => {
            target?.releasePointerCapture?.(pointerId);
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            window.removeEventListener("pointercancel", up);

            if (state.dragScrollAnimationFrame) {
                cancelAnimationFrame(state.dragScrollAnimationFrame);
                state.dragScrollAnimationFrame = 0;
            }

            const seconds = this._getSecondsFromPointer(state, upEvent);
            state.dragging = false;
            state.dragLastPointerEvent = null;
            state.positionSeconds = seconds;
            state.positionAnchorSeconds = seconds;
            state.audioAnchorTime = this._getAudioTimeOrNull();

            this._setDraggedPlayheadPosition(state, upEvent);
            this._updateActiveNotes(state, seconds);

            const wasPlaying = state.isPlaying;
            if (wasPlaying) {
                state.pendingPlaybackSeek = true;
                state.isPlaying = false;
            }

            try {
                await state.dotNetRef?.invokeMethodAsync("PreviewSeekSeconds", seconds);
                await state.dotNetRef?.invokeMethodAsync("CommitSeekSeconds", seconds);
            } catch (error) {
                console.warn("Failed to commit MIDI visualiser seek", error);
            }

            if (!wasPlaying)
                this._updateAnimation(state);
        };

        move(event);
        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up);
        window.addEventListener("pointercancel", up);
    },

    _beginViewportDrag(root, event) {
        if (event.button !== undefined && event.button !== 0)
            return;

        const state = this._states.get(root);
        if (!state || !state.viewport || !state.tracks || state.mode === "Record")
            return;

        event.preventDefault();

        const pointerId = event.pointerId;
        const target = event.currentTarget;
        const startClientX = event.clientX;
        const startScrollLeft = state.viewport.scrollLeft;

        state.viewportDragging = true;
        state.root.classList.add("midi-track-visualiser--viewport-dragging");
        target?.setPointerCapture?.(pointerId);

        const move = moveEvent => {
            const deltaX = moveEvent.clientX - startClientX;
            state.viewport.scrollLeft = this._clamp(
                startScrollLeft - deltaX,
                0,
                this._getMaxScrollLeft(state));

            this._syncPlayheadAfterViewportScroll(state, true);
        };

        const up = () => {
            target?.releasePointerCapture?.(pointerId);
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            window.removeEventListener("pointercancel", up);

            state.viewportDragging = false;
            state.root.classList.remove("midi-track-visualiser--viewport-dragging");
            this._syncPlayheadAfterViewportScroll(state, true);
        };

        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up);
        window.addEventListener("pointercancel", up);
    },


    _beginRecordNoteDrag(state, event, id, startSeconds, durationSeconds, semitoneNumber) {
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
        const startSemitone = this._clamp(
            Number.isFinite(Number(semitoneNumber)) ? Number(semitoneNumber) : state.minSemitone,
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

        const select = async () => {
            try {
                await state.dotNetRef?.invokeMethodAsync("SelectRecordNote", id);
            } catch (error) {
                console.warn("Failed to select record visualiser note", error);
            }
        };

        const move = moveEvent => {
            moveEvent.preventDefault();
            const deltaX = moveEvent.clientX - startClientX;
            const deltaY = moveEvent.clientY - startClientY;

            if (Math.abs(deltaX) >= 3 || Math.abs(deltaY) >= 3)
                moved = true;

            const rawStart = start + (deltaX / state.pixelsPerSecond);
            currentStart = this._snapRecordNoteStart(state, rawStart, maxStart);
            currentSemitone = this._getSemitoneFromPointerY(state, moveEvent.clientY);

            target.style.left = `${currentStart * state.pixelsPerSecond}px`;
            target.style.top = `${this._getNoteTop(state, currentSemitone)}px`;

            if (currentSemitone !== lastPreviewedSemitone) {
                lastPreviewedSemitone = currentSemitone;
                const noteLabel = this._formatNoteFromSemitone(currentSemitone);
                target.title = target.title.replace(/· [A-G][#b]?-?\d+ ·/, `· ${noteLabel} ·`);
                if (label) {
                    label.textContent = noteLabel;
                    this._fitNoteLabel(label, target.getBoundingClientRect().width || this._getNoteWidth(state, duration, noteLabel), noteLabel);
                }

                try {
                    void state.dotNetRef?.invokeMethodAsync("PreviewRecordNoteSemitone", currentSemitone);
                } catch (error) {
                    console.warn("Failed to preview dragged record visualiser note", error);
                }
            }
        };

        const up = async upEvent => {
            upEvent.preventDefault();
            upEvent.stopPropagation();

            target?.releasePointerCapture?.(pointerId);
            target?.classList.remove("midi-track-visualiser__note--dragging");
            state.root.classList.remove("midi-track-visualiser--note-dragging");
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            window.removeEventListener("pointercancel", up);

            const deltaSeconds = currentStart - start;
            const semitoneChanged = currentSemitone !== startSemitone;

            if (moved && (Math.abs(deltaSeconds) > 0.0001 || semitoneChanged)) {
                state.suppressNextNoteClick = true;

                try {
                    await state.dotNetRef?.invokeMethodAsync("MoveRecordNoteAndPitch", id, deltaSeconds, currentSemitone);
                } catch (error) {
                    console.warn("Failed to move record visualiser note", error);
                }
            } else {
                await select();
            }
        };

        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up);
        window.addEventListener("pointercancel", up);
    },


    _beginRecordPlayheadDrag(state, event) {
        if (event.button !== undefined && event.button !== 0)
            return;

        if (!state || state.mode !== "Edit" || !state.viewport || !state.tracks)
            return;

        event.preventDefault();
        event.stopPropagation();

        const pointerId = event.pointerId;
        const target = event.currentTarget;
        state.recordPlayheadDragging = true;
        state.root?.focus?.({ preventScroll: true });
        target?.setPointerCapture?.(pointerId);

        const move = moveEvent => {
            moveEvent.preventDefault();
            const seconds = this._getSecondsFromPointer(state, moveEvent);
            state.positionSeconds = seconds;
            state.positionAnchorSeconds = seconds;
            state.audioAnchorTime = this._getAudioTimeOrNull();
            this._setDraggedPlayheadPosition(state, moveEvent);
            this._updateActiveNotes(state, seconds);
        };

        const up = async upEvent => {
            upEvent.preventDefault();
            upEvent.stopPropagation();
            target?.releasePointerCapture?.(pointerId);
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            window.removeEventListener("pointercancel", up);
            state.recordPlayheadDragging = false;

            const seconds = this._getSecondsFromPointer(state, upEvent);
            state.positionSeconds = seconds;
            state.positionAnchorSeconds = seconds;
            state.audioAnchorTime = this._getAudioTimeOrNull();
            this._setPlayheadPosition(state, seconds, false);

            try {
                await state.dotNetRef?.invokeMethodAsync("SetRecordPositionSeconds", seconds);
            } catch (error) {
                console.warn("Failed to set record visualiser position", error);
            }
        };

        move(event);
        window.addEventListener("pointermove", move);
        window.addEventListener("pointerup", up);
        window.addEventListener("pointercancel", up);
    },

    _getNoteTop(state, semitone) {
        const minSemitone = Number.isFinite(Number(state.minSemitone)) ? Number(state.minSemitone) : 0;
        const maxSemitone = Number.isFinite(Number(state.maxSemitone)) ? Number(state.maxSemitone) : minSemitone;
        const range = Math.max(1, maxSemitone - minSemitone);
        const availableHeight = state.laneHeight - state.noteHeight - 22;
        const clampedSemitone = this._clamp(Number(semitone) || minSemitone, minSemitone, maxSemitone);
        const normalized = (clampedSemitone - minSemitone) / range;

        return 11 + ((1.0 - normalized) * availableHeight);
    },

    _getSemitoneFromPointerY(state, clientY) {
        const rect = state.tracks.getBoundingClientRect();
        const minSemitone = Number.isFinite(Number(state.minSemitone)) ? Number(state.minSemitone) : 0;
        const maxSemitone = Number.isFinite(Number(state.maxSemitone)) ? Number(state.maxSemitone) : minSemitone;
        const range = Math.max(1, maxSemitone - minSemitone);
        const availableHeight = state.laneHeight - state.noteHeight - 22;
        const y = this._clamp(clientY - rect.top - 11, 0, availableHeight);
        const normalized = 1.0 - (y / availableHeight);
        return Math.round(this._clamp(minSemitone + (normalized * range), minSemitone, maxSemitone));
    },

    _snapRecordNoteStart(state, startSeconds, maxStart) {
        const clamped = this._clamp(startSeconds, 0, maxStart);
        const snapDivision = Number(state.snapNoteDivision) || 0;

        if (snapDivision <= 0)
            return clamped;

        const snapSeconds = this._getSecondsPerBeat(state.tempoBpm, snapDivision);
        if (!Number.isFinite(snapSeconds) || snapSeconds <= 0)
            return clamped;

        return this._clamp(Math.round(clamped / snapSeconds) * snapSeconds, 0, maxStart);
    },

    _setDraggedPlayheadPosition(state, event) {
        if (!state.viewport || !state.playhead)
            return;

        const rect = state.viewport.getBoundingClientRect();
        const viewportX = this._clamp(
            event.clientX - rect.left,
            playheadHalfWidth,
            state.viewport.clientWidth - playheadHalfWidth);

        state.dragPlayheadViewportX = viewportX;
        state.playhead.style.transform = `translateX(${viewportX - playheadHalfWidth}px)`;
    },

    _getMaxScrollLeft(state) {
        if (!state.viewport)
            return 0;

        return Math.max(
            0,
            state.tracks.scrollWidth - state.viewport.clientWidth);
    },

    _scrollToPosition(state, seconds) {
        if (!state.viewport)
            return;

        const left = seconds * state.pixelsPerSecond;
        const targetViewportX = state.dragPlayheadViewportX ?? scrollOffset;

        this._setViewportScrollLeft(state, this._clamp(left - targetViewportX, 0, this._getMaxScrollLeft(state)));
    },

    _setViewportScrollLeft(state, scrollLeft) {
        if (!state.viewport)
            return;

        state.suppressViewportScroll = true;
        state.viewport.scrollLeft = this._clamp(scrollLeft, 0, this._getMaxScrollLeft(state));
        window.setTimeout(() => {
            state.suppressViewportScroll = false;
        }, 0);
    },

    _syncPlayheadAfterViewportScroll(state, commit) {
        if (!state || !state.viewport || !state.playhead || state.suppressViewportScroll || state.recordPlayheadDragging || state.dragging || state.mode === "Record")
            return;

        const viewportLeft = state.viewport.scrollLeft;
        const viewportRight = viewportLeft + state.viewport.clientWidth;
        const playheadContentX = state.positionSeconds * state.pixelsPerSecond;
        let nextPosition = null;
        let nextViewportX = null;

        if (playheadContentX < viewportLeft) {
            nextPosition = viewportLeft / state.pixelsPerSecond;
            nextViewportX = playheadHalfWidth;
        } else if (playheadContentX > viewportRight - playheadHalfWidth) {
            nextPosition = (viewportRight - playheadHalfWidth) / state.pixelsPerSecond;
            nextViewportX = state.viewport.clientWidth - playheadHalfWidth;
        }

        if (nextPosition === null) {
            this._setPlayheadPosition(state, state.positionSeconds, false);
            return;
        }

        const seconds = this._clamp(nextPosition, 0, state.durationSeconds || 0);
        state.positionSeconds = seconds;
        state.positionAnchorSeconds = seconds;
        state.audioAnchorTime = this._getAudioTimeOrNull();
        state.dragPlayheadViewportX = nextViewportX;
        state.playhead.style.transform = `translateX(${nextViewportX - playheadHalfWidth}px)`;
        this._updateActiveNotes(state, seconds);

        if (commit)
            void this._commitPositionChangedByViewportScroll(state, seconds);
    },

    async _commitPositionChangedByViewportScroll(state, seconds) {
        try {
            if (state.mode === "Edit" || state.mode === "Record") {
                await state.dotNetRef?.invokeMethodAsync("SetRecordPositionSeconds", seconds);
            } else if (!state.isPlaying) {
                await state.dotNetRef?.invokeMethodAsync("PreviewSeekSeconds", seconds);
                await state.dotNetRef?.invokeMethodAsync("CommitSeekSeconds", seconds);
            }
        } catch (error) {
            console.warn("Failed to sync visualiser playhead after viewport scroll", error);
        }
    },

    _updateDragAutoScroll(state) {
        if (!state.dragging || !state.viewport || !state.dragLastPointerEvent)
            return;

        if (state.dragScrollAnimationFrame)
            return;

        let lastTimestamp = null;

        const tick = timestamp => {
            if (!state.dragging || !state.viewport || !state.dragLastPointerEvent) {
                state.dragScrollAnimationFrame = 0;
                return;
            }

            const rect = state.viewport.getBoundingClientRect();
            const viewportWidth = state.viewport.clientWidth;
            const edgeWidth = Math.max(24, viewportWidth * dragScrollEdgeRatio);
            const pointerX = state.dragLastPointerEvent.clientX - rect.left;

            let direction = 0;
            let intensity = 0;

            if (pointerX >= viewportWidth - edgeWidth) {
                direction = 1;
                intensity = this._clamp((pointerX - (viewportWidth - edgeWidth)) / edgeWidth, 0, 1);
            } else if (pointerX <= edgeWidth) {
                direction = -1;
                intensity = this._clamp((edgeWidth - pointerX) / edgeWidth, 0, 1);
            }

            if (direction !== 0) {
                const deltaSeconds = lastTimestamp === null
                    ? 0
                    : Math.max(0, (timestamp - lastTimestamp) / 1000);

                const oldScrollLeft = state.viewport.scrollLeft;
                const deltaPixels = direction * dragScrollMaxPixelsPerSecond * intensity * deltaSeconds;
                state.viewport.scrollLeft = this._clamp(
                    oldScrollLeft + deltaPixels,
                    0,
                    this._getMaxScrollLeft(state));

                const pointerViewportX = this._clamp(
                    state.dragLastPointerEvent.clientX - rect.left,
                    playheadHalfWidth,
                    state.viewport.clientWidth - playheadHalfWidth);

                const contentX = state.viewport.scrollLeft + pointerViewportX;
                const maxContentX = state.durationSeconds * state.pixelsPerSecond;
                const seconds = this._clamp(
                    Math.min(contentX, maxContentX) / state.pixelsPerSecond,
                    0,
                    state.durationSeconds || 0);

                state.positionSeconds = seconds;
                state.positionAnchorSeconds = seconds;
                state.audioAnchorTime = this._getAudioTimeOrNull();
                state.dragPlayheadViewportX = pointerViewportX;

                if (state.playhead)
                    state.playhead.style.transform = `translateX(${pointerViewportX - playheadHalfWidth}px)`;

                this._updateActiveNotes(state, seconds);
            }

            lastTimestamp = timestamp;
            state.dragScrollAnimationFrame = requestAnimationFrame(tick);
        };

        state.dragScrollAnimationFrame = requestAnimationFrame(tick);
    },

    _updateAnimation(state) {
        if (state.animationFrame) {
            cancelAnimationFrame(state.animationFrame);
            state.animationFrame = 0;
        }

        if (!state.isPlaying || state.dragging || state.pendingPlaybackSeek)
            return;

        if (state.audioAnchorTime === null)
            state.audioAnchorTime = this._getAudioTimeOrNull();

        const tick = () => {
            if (!state.isPlaying || state.dragging || state.pendingPlaybackSeek) {
                state.animationFrame = 0;
                return;
            }

            const audioTime = this._getAudioTimeOrNull();
            if (audioTime !== null && state.audioAnchorTime !== null) {
                const ratio = state.tempoBpm / state.initialMidiBpm;
                const elapsed = Math.max(0, audioTime - state.audioAnchorTime) * ratio;
                const position = this._clamp(state.positionAnchorSeconds + elapsed, 0, state.durationSeconds);
                state.positionSeconds = position;
                this._setPlayheadPosition(state, position, true);

                if (position >= state.durationSeconds) {
                    state.animationFrame = 0;
                    return;
                }
            }

            state.animationFrame = requestAnimationFrame(tick);
        };

        state.animationFrame = requestAnimationFrame(tick);
    },

    _setPlayheadPosition(state, seconds, follow) {
        const clamped = this._clamp(seconds, 0, state.durationSeconds || 0);
        const left = clamped * state.pixelsPerSecond;
        const scrollLeft = state.viewport?.scrollLeft ?? 0;

        let playheadViewportX = left - scrollLeft;

        if (follow && state.viewport && !state.viewportDragging) {
            const targetViewportX = state.dragPlayheadViewportX ?? scrollOffset;

            if (clamped <= 0.0001) {
                this._setViewportScrollLeft(state, 0);
                playheadViewportX = 0;
            } else if (playheadViewportX > targetViewportX) {
                this._setViewportScrollLeft(state, left - targetViewportX);
                playheadViewportX = targetViewportX;
            } else if (playheadViewportX < playheadHalfWidth) {
                this._setViewportScrollLeft(state, Math.max(0, left - playheadHalfWidth));
                playheadViewportX = playheadHalfWidth;
            }
        }

        playheadViewportX = this._clamp(
            playheadViewportX,
            0,
            (state.viewport?.clientWidth ?? 0) - playheadHalfWidth);

        if (state.playhead)
            state.playhead.style.transform = `translateX(${playheadViewportX - playheadHalfWidth}px)`;

        this._updateActiveNotes(state, clamped);
    },

    _positionBarLabels(state) {
        for (const label of state.barLabels || []) {
            const width = label.element.getBoundingClientRect().width || 0;
            label.element.style.left = `${label.left - (width / 2)}px`;
        }
    },

    async _seekToBarline(state, event, seconds) {
        event.preventDefault();
        event.stopPropagation();

        const clamped = this._clamp(Number(seconds) || 0, 0, state.durationSeconds || 0);
        state.positionSeconds = clamped;
        state.positionAnchorSeconds = clamped;
        state.audioAnchorTime = this._getAudioTimeOrNull();
        this._setPlayheadPosition(state, clamped, true);

        const wasPlaying = state.isPlaying;
        if (wasPlaying) {
            state.pendingPlaybackSeek = true;
            state.isPlaying = false;
        }

        try {
            if (state.mode === "Edit" || state.mode === "Record") {
                await state.dotNetRef?.invokeMethodAsync("SetRecordPositionSeconds", clamped);
            } else {
                await state.dotNetRef?.invokeMethodAsync("PreviewSeekSeconds", clamped);
                await state.dotNetRef?.invokeMethodAsync("CommitSeekSeconds", clamped);
            }
        } catch (error) {
            console.warn("Failed to commit MIDI visualiser barline seek", error);
        }

        if (!wasPlaying)
            this._updateAnimation(state);
    },

    _hasPlaybackReachedAudioStart(state) {
        if (!state.isPlaying || state.dragging || state.pendingPlaybackSeek)
            return false;

        if (state.midiStartAt === null)
            return true;

        const audioTime = this._getAudioTimeOrNull();
        return audioTime !== null && audioTime >= state.midiStartAt;
    },

    _updateActiveNotes(state, positionSeconds) {
        if (!Array.isArray(state.noteElements) || state.noteElements.length === 0)
            return;

        if (!this._hasPlaybackReachedAudioStart(state)) {
            for (const element of state.activeNoteElements)
                element.classList.remove("midi-track-visualiser__note--active");

            state.activeNoteElements = new Set();
            return;
        }

        const active = new Set();
        const epsilon = 0.01;

        for (const note of state.noteElements) {
            if (positionSeconds + epsilon >= note.start && positionSeconds <= note.end + epsilon)
                active.add(note.element);
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
    },

    _getNoteWidth(state, durationSeconds, text) {
        const naturalWidth = Math.max(durationSeconds * state.pixelsPerSecond, 8);
        const labelLength = String(text || "").length;

        if (labelLength === 0)
            return naturalWidth;

        const labelWidth = Math.max(minNoteLabelWidth, 14 + (labelLength * 6.25));
        return Math.max(naturalWidth, labelWidth);
    },

    _fitNoteLabel(label, width, text) {
        if (!label || !text) {
            if (label)
                label.style.display = "none";
            return;
        }

        const characters = Math.max(1, String(text).length);
        const horizontalChrome = 10;
        const available = Math.max(1, width - horizontalChrome);
        const fontSizePx = this._clamp(available / Math.max(characters * 0.64, 1), 4, 10.5);

        label.style.fontSize = `${fontSizePx}px`;
        label.style.width = `${available}px`;
        label.style.maxWidth = `${available}px`;
        label.style.display = "block";
    },

    _getSecondsFromPointer(state, event) {
        const rect = state.viewport.getBoundingClientRect();
        const viewportX = this._clamp(
            event.clientX - rect.left,
            playheadHalfWidth,
            state.viewport.clientWidth - playheadHalfWidth);
        const contentX = state.viewport.scrollLeft + viewportX;
        const maxContentX = state.durationSeconds * state.pixelsPerSecond;

        return this._clamp(
            Math.min(contentX, maxContentX) / state.pixelsPerSecond,
            0,
            state.durationSeconds || 0);
    },

    _getAudioTimeOrNull() {
        const audioTime = window.panPlayback?.peekAudioTime?.() ?? window.steelPan?.peekAudioTime?.() ?? null;
        return Number.isFinite(audioTime) ? audioTime : null;
    },

    _getCssIsolationAttribute(root) {
        for (const attribute of root.attributes) {
            if (/^b-[a-z0-9]+$/i.test(attribute.name))
                return attribute.name;
        }

        return null;
    },

    _createScopedElement(state, tagName, className) {
        const element = document.createElement(tagName);

        if (className)
            element.className = className;

        if (state.scopeAttribute)
            element.setAttribute(state.scopeAttribute, "");

        return element;
    },

    _getSecondsPerBeat(bpm, beatUnit) {
        const safeBpm = Math.max(Number(bpm) || 120, 1);
        const safeBeatUnit = Math.max(Number(beatUnit) || 4, 1);
        return (60.0 / safeBpm) * (4.0 / safeBeatUnit);
    },

    _formatNoteFromSemitone(semitoneNumber) {
        const safe = Math.round(Number(semitoneNumber) || 0);
        const names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        const pitchClass = ((safe % 12) + 12) % 12;
        const octave = Math.floor(safe / 12);
        return `${names[pitchClass]}${octave}`;
    },

    _clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
    },

    _formatTime(totalSeconds) {
        const safe = Math.max(0, Number(totalSeconds) || 0);
        const minutes = Math.floor(safe / 60);
        const seconds = Math.floor(safe % 60);
        const millis = Math.floor((safe - Math.floor(safe)) * 1000);
        return `${minutes}:${String(seconds).padStart(2, "0")}.${String(millis).padStart(3, "0")}`;
    }
};
