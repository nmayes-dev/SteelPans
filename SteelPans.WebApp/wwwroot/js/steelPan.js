window.steelPan = {
    _refs: {},
    _audioBuffers: {},
    _audioContext: null,
    _scheduledSources: [],
    _metronomeCtx: null,
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

    _getSamplePath: function (noteKey) {
        const normalized = this._normalizeEnharmonic(noteKey);
        return `/audio/samples/${encodeURIComponent(normalized)}.wav`;
    },

    preloadNotes: async function (noteKeys) {
        if (!Array.isArray(noteKeys))
            return;

        const ctx = this._getAudioContext();

        for (const noteKey of noteKeys) {
            const path = this._getSamplePath(noteKey);

            if (this._audioBuffers[path])
                continue;

            const response = await fetch(path);
            const arrayBuffer = await response.arrayBuffer();
            const audioBuffer = await ctx.decodeAudioData(arrayBuffer);

            this._audioBuffers[path] = audioBuffer;
        }
    },

    playNote: async function (noteKey) {
        const ctx = this._getAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        const path = this._getSamplePath(noteKey);
        let buffer = this._audioBuffers[path];

        if (!buffer) {
            const response = await fetch(path);
            const arrayBuffer = await response.arrayBuffer();
            buffer = await ctx.decodeAudioData(arrayBuffer);
            this._audioBuffers[path] = buffer;
        }

        const source = ctx.createBufferSource();
        source.buffer = buffer;
        source.connect(ctx.destination);
        source.start();
    },

    playNotes: async function (noteKeys) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        const ctx = this._getAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        for (const noteKey of noteKeys) {
            await this.playNote(noteKey);
        }
    },

    steelPanNoteClick: async function (element, componentId, noteKey, event) {
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

        ref.invokeMethodAsync("HandleNoteInteraction", noteKey, shift);
    },

    playMidiSchedule: async function (actions) {
        if (!Array.isArray(actions) || actions.length === 0)
            return;

        const ctx = this._getAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        const startAt = ctx.currentTime + 0.05;

        for (const action of actions) {
            if (!action.isNoteOn)
                continue;

            const path = this._getSamplePath(action.noteKey);
            let buffer = this._audioBuffers[path];

            if (!buffer) {
                const response = await fetch(path);
                if (!response.ok) {
                    console.error("Failed to fetch sample", path, response.status);
                    continue;
                }

                const arrayBuffer = await response.arrayBuffer();
                buffer = await ctx.decodeAudioData(arrayBuffer);
                this._audioBuffers[path] = buffer;
            }

            const source = ctx.createBufferSource();
            source.buffer = buffer;
            source.connect(ctx.destination);
            source.start(startAt + action.timeSeconds);
            this._scheduledSources.push(source);

            source.onended = () => {
                const index = this._scheduledSources.indexOf(source);
                if (index >= 0)
                    this._scheduledSources.splice(index, 1);
            };
        }

        return startAt;
    },

    stopMidiSchedule: function () {
        for (const source of this._scheduledSources) {
            try {
                source.stop();
            } catch {
            }
        }

        this._scheduledSources = [];
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
            if (note === "E") return "F" + octave;
            if (note === "B") return "C" + (parseInt(octave, 10) + 1);
        }

        return note + accidental + octave;
    },

    _getMetronomeContext: function () {
        if (!this._metronomeCtx) {
            this._metronomeCtx = new (window.AudioContext || window.webkitAudioContext)();
        }

        return this._metronomeCtx;
    },

    playMetronomeTick: async function (isAccent) {
        const ctx = this._getMetronomeContext();

        if (ctx.state === "suspended") {
            await ctx.resume();
        }

        const now = ctx.currentTime;

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

    stopMetronome: function () {
        for (const node of this._metronomeNodes) {
            try {
                node.stop();
            } catch {
            }
        }

        this._metronomeNodes = [];
    },

    beginMetronomeWeightDrag: function (trackElement, dotNetRef, initialClientY) {
        if (!trackElement || !dotNetRef) {
            return;
        }

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