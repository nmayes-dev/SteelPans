window.steelPan = {
    _refs: {},
    _audioBuffers: {},
    _audioContext: null,
    _scheduledSources: [],
    _scheduledSourcesByNote: {},
    _scheduledVisualTimers: [],
    _panElements: {},
    _metronomeNodes: [],

    register: function (id, dotNetRef) {
        this._refs[id] = dotNetRef;
    },

    unregister: function (id) {
        delete this._refs[id];
        delete this._panElements[id];
        this.clearPlayingVisuals(id);
    },

    bindPanElements: function (componentId) {
        if (!componentId)
            return;

        const root = document.querySelector(`[data-steelpan-id="${componentId}"]`) ||
            document.getElementById(componentId) ||
            document.querySelector(`.steel-pan [data-component-id="${componentId}"]`);

        const noteElements = {};
        const labelElements = {};

        const allNoteEls = document.querySelectorAll(`[data-pan-note][data-pan-component="${componentId}"]`);
        const allLabelEls = document.querySelectorAll(`[data-pan-label][data-pan-component="${componentId}"]`);

        for (const el of allNoteEls) {
            const key = el.getAttribute("data-pan-note");
            if (key)
                noteElements[key] = el;
        }

        for (const el of allLabelEls) {
            const key = el.getAttribute("data-pan-label");
            if (key)
                labelElements[key] = el;
        }

        this._panElements[componentId] = {
            noteElements: noteElements,
            labelElements: labelElements
        };
    },

    _getAudioContext: function () {
        if (!this._audioContext) {
            this._audioContext = new (window.AudioContext || window.webkitAudioContext)();
        }

        return this._audioContext;
    },

    getAudioTime: function () {
        const ctx = this._getAudioContext();
        return ctx.currentTime;
    },

    _getSamplePath: function (noteKey) {
        const normalized = this._normalizeEnharmonic(noteKey);
        return `/audio/samples/${encodeURIComponent(normalized)}.wav`;
    },

    _loadBuffer: async function (noteKey) {
        const ctx = this._getAudioContext();
        const path = this._getSamplePath(noteKey);

        let buffer = this._audioBuffers[path];
        if (buffer)
            return buffer;

        const response = await fetch(path);
        const arrayBuffer = await response.arrayBuffer();
        buffer = await ctx.decodeAudioData(arrayBuffer);

        this._audioBuffers[path] = buffer;
        return buffer;
    },

    ensureAudioReady: async function () {
        const ctx = this._getAudioContext();

        if (ctx.state === "suspended") {
            await ctx.resume();
        }
    },

    preloadNotes: async function (noteKeys) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        await Promise.all(noteKeys.map(noteKey => this._loadBuffer(noteKey)));
    },

    playNote: async function (noteKey) {
        await this.ensureAudioReady();

        const ctx = this._getAudioContext();
        const buffer = await this._loadBuffer(noteKey);

        const source = ctx.createBufferSource();
        source.buffer = buffer;
        source.connect(ctx.destination);
        source.start();

        return true;
    },

    playNotes: async function (noteKeys) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        await this.ensureAudioReady();

        const ctx = this._getAudioContext();

        for (const noteKey of noteKeys) {
            const buffer = await this._loadBuffer(noteKey);

            const source = ctx.createBufferSource();
            source.buffer = buffer;
            source.connect(ctx.destination);
            source.start();
        }
    },

    _getBoundPan: function (componentId) {
        return this._panElements[componentId] || null;
    },

    _setNotePlaying: function (componentId, noteKey, isPlaying) {
        const pan = this._getBoundPan(componentId);
        if (!pan)
            return;

        const noteEl = pan.noteElements[noteKey];
        const labelEl = pan.labelElements[noteKey];

        if (noteEl) {
            noteEl.classList.toggle("sp-note--on", !!isPlaying);
        }

        if (labelEl) {
            labelEl.classList.toggle("sp-label--on", !!isPlaying);
        }
    },

    clearPlayingVisuals: function (componentId, noteKeys) {
        const pan = this._getBoundPan(componentId);
        if (!pan)
            return;

        if (Array.isArray(noteKeys) && noteKeys.length > 0) {
            for (const noteKey of noteKeys) {
                this._setNotePlaying(componentId, noteKey, false);
            }
            return;
        }

        for (const noteKey of Object.keys(pan.noteElements)) {
            this._setNotePlaying(componentId, noteKey, false);
        }
    },

    flashNotes: function (componentId, noteKeys, durationMs) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        for (const noteKey of noteKeys) {
            this._setNotePlaying(componentId, noteKey, true);
        }

        const timeoutId = window.setTimeout(() => {
            for (const noteKey of noteKeys) {
                this._setNotePlaying(componentId, noteKey, false);
            }
        }, Math.max(1, durationMs || 120));

        this._scheduledVisualTimers.push(timeoutId);
    },

    notePointerDown: async function (noteElement, labelElement, componentId, noteKey, event) {
        if (event.pointerType === "mouse" && event.button !== 0)
            return;

        event.stopPropagation();
        event.preventDefault();

        const ref = this._refs[componentId];
        if (!ref)
            return;

        await this.playNote(noteKey);

        this._setNotePlaying(componentId, noteKey, true);

        const timeoutId = window.setTimeout(() => {
            this._setNotePlaying(componentId, noteKey, false);
        }, 120);

        this._scheduledVisualTimers.push(timeoutId);
    },

    playMidiSchedule: async function (componentId, scheduledActions) {
        if (!Array.isArray(scheduledActions) || scheduledActions.length === 0)
            return null;

        await this.ensureAudioReady();

        const ctx = this._getAudioContext();

        this.stopMidiSchedule(componentId);

        const uniqueNoteKeys = [...new Set(
            scheduledActions
                .map(action => action?.noteKey)
                .filter(noteKey => typeof noteKey === "string" && noteKey.length > 0)
        )];

        for (const noteKey of uniqueNoteKeys) {
            await this._loadBuffer(noteKey);
        }

        const startAt = ctx.currentTime + 0.05;
        this._scheduledSourcesByNote = {};

        const sortedActions = scheduledActions
            .filter(action =>
                action &&
                typeof action.noteKey === "string" &&
                typeof action.timeSeconds === "number" &&
                typeof action.isNoteOn === "boolean")
            .sort((a, b) => a.timeSeconds - b.timeSeconds);

        for (const action of sortedActions) {
            if (!action.isNoteOn)
                continue;

            const when = startAt + action.timeSeconds;
            const buffer = await this._loadBuffer(action.noteKey);

            const source = ctx.createBufferSource();
            source.buffer = buffer;
            source.connect(ctx.destination);
            source.start(when);

            const entry = {
                noteKey: action.noteKey,
                source: source,
                startTime: when
            };

            this._scheduledSources.push(entry);

            if (!this._scheduledSourcesByNote[action.noteKey]) {
                this._scheduledSourcesByNote[action.noteKey] = [];
            }

            this._scheduledSourcesByNote[action.noteKey].push(entry);

            source.onended = () => {
                const scheduledIndex = this._scheduledSources.indexOf(entry);
                if (scheduledIndex >= 0) {
                    this._scheduledSources.splice(scheduledIndex, 1);
                }

                const perNote = this._scheduledSourcesByNote[action.noteKey];
                if (perNote) {
                    const noteIndex = perNote.indexOf(entry);
                    if (noteIndex >= 0) {
                        perNote.splice(noteIndex, 1);
                    }

                    if (perNote.length === 0) {
                        delete this._scheduledSourcesByNote[action.noteKey];
                    }
                }
            };
        }

        const noteOffActions = sortedActions.filter(action => !action.isNoteOn);

        for (const action of noteOffActions) {
            const perNote = this._scheduledSourcesByNote[action.noteKey];
            if (!perNote || perNote.length === 0)
                continue;

            const when = startAt + action.timeSeconds;

            let candidateIndex = -1;
            for (let i = 0; i < perNote.length; i++) {
                if (perNote[i].startTime <= when) {
                    candidateIndex = i;
                    break;
                }
            }

            if (candidateIndex < 0)
                continue;

            const entry = perNote[candidateIndex];

            try {
                entry.source.stop(when);
            } catch {
            }
        }

        for (const action of sortedActions) {
            const delayMs = Math.max(0, (startAt + action.timeSeconds - ctx.currentTime) * 1000.0);

            const timeoutId = window.setTimeout(() => {
                this._setNotePlaying(componentId, action.noteKey, action.isNoteOn);
            }, delayMs);

            this._scheduledVisualTimers.push(timeoutId);
        }

        return startAt;
    },

    playMetronomeSchedule: async function (actions, startAt) {
        if (!Array.isArray(actions) || actions.length === 0)
            return null;

        await this.ensureAudioReady();

        const ctx = this._getAudioContext();
        const actualStartAt = startAt ?? (ctx.currentTime + 0.05);

        for (const action of actions) {
            if (!action || typeof action.timeSeconds !== "number")
                continue;

            const when = actualStartAt + action.timeSeconds;

            const oscillator = ctx.createOscillator();
            const gain = ctx.createGain();

            oscillator.type = "square";
            oscillator.frequency.value = action.isAccent ? 1400 : 1000;

            gain.gain.setValueAtTime(0.0001, when);
            gain.gain.exponentialRampToValueAtTime(action.isAccent ? 0.25 : 0.15, when + 0.002);
            gain.gain.exponentialRampToValueAtTime(0.0001, when + 0.06);

            oscillator.connect(gain);
            gain.connect(ctx.destination);

            oscillator.start(when);
            oscillator.stop(when + 0.07);

            this._metronomeNodes.push(oscillator);

            oscillator.onended = () => {
                const index = this._metronomeNodes.indexOf(oscillator);
                if (index >= 0) {
                    this._metronomeNodes.splice(index, 1);
                }
            };
        }

        return actualStartAt;
    },

    playMetronomeTick: async function (isAccent, when) {
        await this.ensureAudioReady();

        const ctx = this._getAudioContext();
        const now = when ?? ctx.currentTime;

        const oscillator = ctx.createOscillator();
        const gain = ctx.createGain();

        oscillator.type = "square";
        oscillator.frequency.value = isAccent ? 1400 : 1000;

        gain.gain.setValueAtTime(0.0001, now);
        gain.gain.exponentialRampToValueAtTime(isAccent ? 0.25 : 0.15, now + 0.002);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.06);

        oscillator.connect(gain);
        gain.connect(ctx.destination);

        oscillator.start(now);
        oscillator.stop(now + 0.07);

        this._metronomeNodes.push(oscillator);

        oscillator.onended = () => {
            const index = this._metronomeNodes.indexOf(oscillator);
            if (index >= 0) {
                this._metronomeNodes.splice(index, 1);
            }
        };
    },

    stopMidiSchedule: function (componentId) {
        for (const entry of this._scheduledSources) {
            try {
                entry.source.stop();
            } catch {
            }
        }

        for (const timerId of this._scheduledVisualTimers) {
            window.clearTimeout(timerId);
        }

        this._scheduledSources = [];
        this._scheduledSourcesByNote = {};
        this._scheduledVisualTimers = [];

        if (componentId) {
            this.clearPlayingVisuals(componentId);
        }
    },

    stopMetronome: function () {
        for (const node of this._metronomeNodes) {
            try {
                node.stop();
            } catch {
            }
        }

        this._metronomeNodes = [];
    },

    _normalizeEnharmonic: function (noteKey) {
        const match = /^([A-G])([#b]?)(-?\d+)$/.exec(noteKey);
        if (!match)
            return noteKey;

        let [, note, accidental, octave] = match;

        if (accidental === "b") {
            const flatToSharp = {
                "Ab": "G#",
                "Bb": "A#",
                "Cb": "B",
                "Db": "C#",
                "Eb": "D#",
                "Fb": "E",
                "Gb": "F#"
            };

            const key = note + "b";
            if (flatToSharp[key])
                return flatToSharp[key] + octave;
        }

        if (accidental === "#") {
            if (note === "E")
                return "F" + octave;

            if (note === "B")
                return "C" + (parseInt(octave, 10) + 1);
        }

        return note + accidental + octave;
    },

    beginMetronomeWeightDrag: function (trackElement, dotNetRef, initialClientY) {
        if (!trackElement || !dotNetRef)
            return;

        const update = (clientY) => {
            const rect = trackElement.getBoundingClientRect();
            const localY = clientY - rect.top;
            dotNetRef.invokeMethodAsync("OnMetronomeWeightDragged", localY, rect.height);
        };

        const onPointerMove = (event) => {
            update(event.clientY);
        };

        const stop = () => {
            window.removeEventListener("pointermove", onPointerMove);
            window.removeEventListener("pointerup", stop);
            window.removeEventListener("pointercancel", stop);
        };

        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", stop);
        window.addEventListener("pointercancel", stop);

        update(initialClientY);
    }
};