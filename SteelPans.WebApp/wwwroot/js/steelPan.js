window.steelPan = {
    _refs: {},
    _audioBuffers: {},
    _audioContext: null,
    _scheduledSources: [],
    _scheduledSourcesByNote: {},
    _metronomeNodes: [],

    register: function (id, dotNetRef) {
        this._refs[id] = dotNetRef;
    },

    unregister: function (id) {
        delete this._refs[id];
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

    noteClick: async function (element, componentId, noteKey, event) {
        event.stopPropagation();

        const ref = this._refs[componentId];
        if (!ref)
            return;

        const shift = !!event.shiftKey;

        if (!shift && !element.classList.contains("sp-note--active")) {
            await this.playNote(noteKey);

            element.classList.add("sp-note--flash");

            setTimeout(() => {
                element.classList.remove("sp-note--flash");
            }, 120);
        }

        if (shift)
            ref.invokeMethodAsync("HandleNoteInteraction", noteKey);
    },

    playMidiSchedule: async function (scheduledActions) {
        if (!Array.isArray(scheduledActions) || scheduledActions.length === 0)
            return null;

        await this.ensureAudioReady();

        const ctx = this._getAudioContext();

        this.stopMidiSchedule();

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

        const noteOnActions = scheduledActions
            .filter(action =>
                action &&
                action.isNoteOn === true &&
                typeof action.noteKey === "string" &&
                typeof action.timeSeconds === "number")
            .sort((a, b) => a.timeSeconds - b.timeSeconds);

        for (const action of noteOnActions) {
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

        const noteOffActions = scheduledActions
            .filter(action =>
                action &&
                action.isNoteOn === false &&
                typeof action.noteKey === "string" &&
                typeof action.timeSeconds === "number")
            .sort((a, b) => a.timeSeconds - b.timeSeconds);

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

    stopMidiSchedule: function () {
        for (const entry of this._scheduledSources) {
            try {
                entry.source.stop();
            } catch {
            }
        }

        this._scheduledSources = [];
        this._scheduledSourcesByNote = {};
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

        const rect = trackElement.getBoundingClientRect();

        const update = (clientY) => {
            const localY = clientY - rect.top;
            dotNetRef.invokeMethodAsync("OnMetronomeWeightDragged", localY);
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