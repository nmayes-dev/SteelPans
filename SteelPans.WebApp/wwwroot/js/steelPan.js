window.steelPan = {
    _refs: {},
    _audioBuffers: {},
    _audioContext: null,
    _gainByComponent: {},
    _volumeByComponent: {},
    _scheduledSourcesByComponent: {},
    _scheduledSourcesByNoteByComponent: {},
    _scheduledVisualTimersByComponent: {},
    _panElements: {},
    _metronomeNodes: [],
    _midiScheduleStateByComponent: {},

    register: function (id, dotNetRef) {
        this._refs[id] = dotNetRef;
    },

    unregister: function (id) {
        delete this._refs[id];
        delete this._panElements[id];
        delete this._volumeByComponent[id];

        const gain = this._gainByComponent[id];
        if (gain) {
            try {
                gain.disconnect();
            } catch { }
            delete this._gainByComponent[id];
        }

        this.stopMidiSchedule(id);
        delete this._midiScheduleStateByComponent[id];

        this.clearPlayingVisuals(id);
    },

    _ensureAudioContext: function () {
        if (!this._audioContext) {
            const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
            this._audioContext = new AudioContextCtor();
        }

        return this._audioContext;
    },

    _resumeAudioContext: async function () {
        const ctx = this._ensureAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        return ctx;
    },

    unlockAudio: async function () {
        await this._resumeAudioContext();
    },

    _getOrCreateComponentGain: function (componentId) {
        const audioContext = this._ensureAudioContext();

        let gain = this._gainByComponent[componentId];
        if (!gain) {
            gain = audioContext.createGain();
            gain.gain.value = this._volumeByComponent[componentId] ?? 1.0;
            gain.connect(audioContext.destination);
            this._gainByComponent[componentId] = gain;
        }

        return gain;
    },

    setComponentVolume: function (componentId, volume) {
        if (!componentId)
            return;

        const numericVolume = Number(volume);
        const clampedVolume = Number.isFinite(numericVolume)
            ? Math.max(0, Math.min(1, numericVolume))
            : 1.0;

        this._volumeByComponent[componentId] = clampedVolume;

        if (!this._audioContext)
            return;

        const gain = this._gainByComponent[componentId];
        if (!gain)
            return;

        const now = this._audioContext.currentTime;

        gain.gain.cancelScheduledValues(now);
        gain.gain.setValueAtTime(gain.gain.value, now);
        gain.gain.linearRampToValueAtTime(clampedVolume, now + 0.03);
    },

    bindPanElements: function (componentId) {
        if (!componentId)
            return;

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

    getAudioTime: async function () {
        const ctx = await this._resumeAudioContext();
        return ctx.currentTime;
    },

    peekAudioTime: function () {
        const ctx = this._ensureAudioContext();
        return ctx.currentTime;
    },

    _getSamplePath: function (noteKey) {
        const normalized = this._normalizeEnharmonic(noteKey);
        return `/audio/samples/${encodeURIComponent(normalized)}.wav`;
    },

    _loadBuffer: async function (noteKey) {
        const ctx = this._ensureAudioContext();
        const path = this._getSamplePath(noteKey);

        let buffer = this._audioBuffers[path];
        if (buffer)
            return buffer;

        const response = await fetch(path);
        if (!response.ok)
            throw new Error(`Failed to load sample ${path}: ${response.status} ${response.statusText}`);

        const arrayBuffer = await response.arrayBuffer();
        buffer = await ctx.decodeAudioData(arrayBuffer);

        this._audioBuffers[path] = buffer;
        return buffer;
    },

    preloadNotes: async function (noteKeys) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        await this._resumeAudioContext();

        const uniqueNoteKeys = [
            ...new Set(noteKeys.filter(noteKey =>
                typeof noteKey === "string" && noteKey.length > 0))
        ];

        const results = await Promise.allSettled(
            uniqueNoteKeys.map(noteKey => this._loadBuffer(noteKey))
        );

        for (let i = 0; i < results.length; i++) {
            if (results[i].status === "rejected")
                console.warn("Failed to preload note sample", uniqueNoteKeys[i], results[i].reason);
        }
    },

    playNote: async function (componentId, noteKey) {
        const ctx = await this._resumeAudioContext();
        const buffer = await this._loadBuffer(noteKey);

        const source = ctx.createBufferSource();
        source.buffer = buffer;

        const componentGain = this._getOrCreateComponentGain(componentId);
        source.connect(componentGain);

        source.start();

        return true;
    },

    playNotes: async function (componentId, noteKeys) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        const ctx = await this._resumeAudioContext();
        const componentGain = this._getOrCreateComponentGain(componentId);

        for (const noteKey of noteKeys) {
            const buffer = await this._loadBuffer(noteKey);

            const source = ctx.createBufferSource();
            source.buffer = buffer;
            source.connect(componentGain);
            source.start();
        }
    },

    _getBoundPan: function (componentId) {
        return this._panElements[componentId] || null;
    },

    _parseNoteKey: function (noteKey) {
        const match = /^([A-G])([#b]?)(-?\d+)$/.exec(noteKey);
        if (!match)
            return null;

        return {
            letter: match[1],
            accidental: match[2] || "",
            octave: parseInt(match[3], 10)
        };
    },

    _toSemitone: function (noteKey) {
        const parsed = this._parseNoteKey(noteKey);
        if (!parsed)
            return null;

        const baseSemitones = {
            C: 0,
            D: 2,
            E: 4,
            F: 5,
            G: 7,
            A: 9,
            B: 11
        };

        let semitone = baseSemitones[parsed.letter];
        if (parsed.accidental === "#")
            semitone += 1;
        else if (parsed.accidental === "b")
            semitone -= 1;

        let octave = parsed.octave;

        while (semitone < 0) {
            semitone += 12;
            octave -= 1;
        }

        while (semitone >= 12) {
            semitone -= 12;
            octave += 1;
        }

        return {
            semitone: semitone,
            octave: octave
        };
    },

    _fromSemitoneSharp: function (semitone, octave) {
        const names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return `${names[semitone]}${octave}`;
    },

    _fromSemitoneFlat: function (semitone, octave) {
        const names = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
        return `${names[semitone]}${octave}`;
    },

    _normalizeEnharmonic: function (noteKey) {
        const semitoneInfo = this._toSemitone(noteKey);
        if (!semitoneInfo)
            return noteKey;

        return this._fromSemitoneSharp(semitoneInfo.semitone, semitoneInfo.octave);
    },

    _getEquivalentNoteKeys: function (noteKey) {
        const semitoneInfo = this._toSemitone(noteKey);
        if (!semitoneInfo)
            return [noteKey];

        const result = [];
        const seen = new Set();

        const add = (key) => {
            if (!seen.has(key)) {
                seen.add(key);
                result.push(key);
            }
        };

        add(noteKey);
        add(this._fromSemitoneSharp(semitoneInfo.semitone, semitoneInfo.octave));
        add(this._fromSemitoneFlat(semitoneInfo.semitone, semitoneInfo.octave));

        return result;
    },

    _getPanTargetsForNoteKey: function (pan, noteKey) {
        const noteElements = [];
        const labelElements = [];
        const noteSeen = new Set();
        const labelSeen = new Set();

        for (const key of this._getEquivalentNoteKeys(noteKey)) {
            const noteEl = pan.noteElements[key];
            if (noteEl && !noteSeen.has(noteEl)) {
                noteSeen.add(noteEl);
                noteElements.push(noteEl);
            }

            const labelEl = pan.labelElements[key];
            if (labelEl && !labelSeen.has(labelEl)) {
                labelSeen.add(labelEl);
                labelElements.push(labelEl);
            }
        }

        return {
            noteElements: noteElements,
            labelElements: labelElements
        };
    },

    _setNotePlaying: function (componentId, noteKey, isPlaying) {
        const pan = this._getBoundPan(componentId);
        if (!pan)
            return;

        const targets = this._getPanTargetsForNoteKey(pan, noteKey);

        for (const noteEl of targets.noteElements) {
            noteEl.classList.toggle("sp-note--on", !!isPlaying);
        }

        for (const labelEl of targets.labelElements) {
            labelEl.classList.toggle("sp-label--on", !!isPlaying);
        }
    },

    _ensureComponentScheduleState: function (componentId) {
        if (!this._scheduledSourcesByComponent[componentId])
            this._scheduledSourcesByComponent[componentId] = [];

        if (!this._scheduledSourcesByNoteByComponent[componentId])
            this._scheduledSourcesByNoteByComponent[componentId] = {};

        if (!this._scheduledVisualTimersByComponent[componentId])
            this._scheduledVisualTimersByComponent[componentId] = [];
    },

    _getOrCreateMidiScheduleState: function (componentId) {
        let state = this._midiScheduleStateByComponent[componentId];
        if (!state) {
            state = {
                actions: [],
                baseBpm: 120,
                currentBpm: 120,
                anchorAudioTime: 0,
                anchorBeat: 0,
                nextActionIndex: 0,
                isRunning: false,
                schedulerTimerId: null,
                lookaheadSeconds: 0.12,
                schedulerIntervalMs: 25
            };

            this._midiScheduleStateByComponent[componentId] = state;
        }

        return state;
    },

    _getAudioTimeForBeat: function (state, beat) {
        return state.anchorAudioTime + ((beat - state.anchorBeat) * 60.0 / state.currentBpm);
    },

    _getBeatAtAudioTime: function (state, audioTime) {
        return state.anchorBeat + ((audioTime - state.anchorAudioTime) * state.currentBpm / 60.0);
    },

    _removeScheduledSourceEntry: function (componentId, entry) {
        const sources = this._scheduledSourcesByComponent[componentId];
        if (sources) {
            const scheduledIndex = sources.indexOf(entry);
            if (scheduledIndex >= 0)
                sources.splice(scheduledIndex, 1);
        }

        const perNoteMap = this._scheduledSourcesByNoteByComponent[componentId];
        if (!perNoteMap)
            return;

        const perNote = perNoteMap[entry.noteKey];
        if (!perNote)
            return;

        const noteIndex = perNote.indexOf(entry);
        if (noteIndex >= 0)
            perNote.splice(noteIndex, 1);

        if (perNote.length === 0)
            delete perNoteMap[entry.noteKey];
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

        for (const noteKey of Object.keys(pan.labelElements)) {
            this._setNotePlaying(componentId, noteKey, false);
        }
    },

    flashNotes: function (componentId, noteKeys, durationMs) {
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        this._ensureComponentScheduleState(componentId);

        for (const noteKey of noteKeys) {
            this._setNotePlaying(componentId, noteKey, true);
        }

        const timeoutId = window.setTimeout(() => {
            for (const noteKey of noteKeys) {
                this._setNotePlaying(componentId, noteKey, false);
            }
        }, Math.max(1, durationMs || 120));

        this._scheduledVisualTimersByComponent[componentId].push(timeoutId);
    },

    notePointerDown: async function (noteElement, labelElement, componentId, noteKey, event) {
        if (event.pointerType === "mouse" && event.button !== 0)
            return;

        event.stopPropagation();
        event.preventDefault();

        const ref = this._refs[componentId];
        if (!ref)
            return;

        await this.playNote(componentId, noteKey);

        this._setNotePlaying(componentId, noteKey, true);

        const timeoutId = window.setTimeout(() => {
            this._setNotePlaying(componentId, noteKey, false);
        }, 120);

        this._ensureComponentScheduleState(componentId);
        this._scheduledVisualTimersByComponent[componentId].push(timeoutId);
    },

    playMidiSchedule: async function (componentId, scheduledActions, baseBpm, currentBpm, startAt) {
        if (!Array.isArray(scheduledActions) || scheduledActions.length === 0)
            return null;

        const ctx = await this._resumeAudioContext();

        this.stopMidiSchedule(componentId);
        this._ensureComponentScheduleState(componentId);

        const numericBaseBpm = Number(baseBpm);
        const numericCurrentBpm = Number(currentBpm);

        const effectiveBaseBpm = Number.isFinite(numericBaseBpm) && numericBaseBpm > 0
            ? numericBaseBpm
            : 120;

        const effectiveCurrentBpm = Number.isFinite(numericCurrentBpm) && numericCurrentBpm > 0
            ? numericCurrentBpm
            : effectiveBaseBpm;

        const numericStartAt = Number(startAt);
        const hasProvidedStartAt = Number.isFinite(numericStartAt);

        const actualStartAt = hasProvidedStartAt
            ? numericStartAt
            : ctx.currentTime + 0.75;

        console.log("Provided startAt", numericStartAt);
        console.log("Current time", ctx.currentTime);
        console.log("Scheduling MIDI actions for component", componentId, "starting at audio time", actualStartAt);

        const state = this._getOrCreateMidiScheduleState(componentId);
        state.baseBpm = effectiveBaseBpm;
        state.currentBpm = effectiveCurrentBpm;
        state.nextActionIndex = 0;
        state.isRunning = true;

        if (hasProvidedStartAt && actualStartAt <= ctx.currentTime) {
            const lateBySeconds = ctx.currentTime - actualStartAt;

            state.anchorAudioTime = ctx.currentTime;
            state.anchorBeat = lateBySeconds * effectiveCurrentBpm / 60.0;

            console.warn("Joining MIDI schedule late", {
                componentId,
                providedStartAt: actualStartAt,
                currentTime: ctx.currentTime,
                lateBySeconds,
                anchorBeat: state.anchorBeat
            });
        } else {
            state.anchorAudioTime = actualStartAt;
            state.anchorBeat = 0;
        }

        state.actions = scheduledActions
            .filter(action =>
                action &&
                typeof action.noteKey === "string" &&
                action.noteKey.length > 0 &&
                typeof action.timeSeconds === "number" &&
                typeof action.isNoteOn === "boolean")
            .map(action => ({
                noteKey: action.noteKey,
                isNoteOn: action.isNoteOn,
                beat: action.timeSeconds * effectiveBaseBpm / 60.0
            }))
            .sort((a, b) => a.beat - b.beat);

        this._startMidiScheduler(componentId);

        return actualStartAt;
    },

    _startMidiScheduler: function (componentId) {
        const state = this._midiScheduleStateByComponent[componentId];
        if (!state)
            return;

        if (state.schedulerTimerId != null) {
            window.clearInterval(state.schedulerTimerId);
            state.schedulerTimerId = null;
        }

        const tick = async () => {
            const liveState = this._midiScheduleStateByComponent[componentId];
            if (!liveState || !liveState.isRunning)
                return;

            const ctx = this._ensureAudioContext();

            await this._scheduleMidiWindow(
                componentId,
                ctx.currentTime,
                ctx.currentTime + liveState.lookaheadSeconds);

            if (liveState.nextActionIndex >= liveState.actions.length) {
                const activeSources = this._scheduledSourcesByComponent[componentId] || [];
                if (activeSources.length === 0) {
                    if (liveState.schedulerTimerId != null) {
                        window.clearInterval(liveState.schedulerTimerId);
                        liveState.schedulerTimerId = null;
                    }

                    liveState.isRunning = false;
                }
            }
        };

        state.schedulerTimerId = window.setInterval(() => {
            tick().catch(error => console.warn("MIDI scheduler tick failed", error));
        }, state.schedulerIntervalMs);

        tick().catch(error => console.warn("MIDI scheduler tick failed", error));
    },

    _scheduleMidiWindow: async function (componentId, windowStart, windowEnd) {
        const state = this._midiScheduleStateByComponent[componentId];
        if (!state || !state.isRunning)
            return;

        const ctx = this._ensureAudioContext();
        const componentGain = this._getOrCreateComponentGain(componentId);

        const sources = this._scheduledSourcesByComponent[componentId];
        const perNoteMap = this._scheduledSourcesByNoteByComponent[componentId];
        const timers = this._scheduledVisualTimersByComponent[componentId];

        while (state.nextActionIndex < state.actions.length) {
            const action = state.actions[state.nextActionIndex];
            const when = this._getAudioTimeForBeat(state, action.beat);

            if (when > windowEnd)
                break;

            if (when >= windowStart - 0.005) {
                if (action.isNoteOn) {
                    const buffer = await this._loadBuffer(action.noteKey);
                    const source = ctx.createBufferSource();

                    source.buffer = buffer;
                    source.connect(componentGain);
                    source.start(Math.max(when, ctx.currentTime));

                    const normalizedNoteKey = this._normalizeEnharmonic(action.noteKey);

                    const entry = {
                        noteKey: normalizedNoteKey,
                        source: source,
                        startTime: when
                    };

                    sources.push(entry);

                    if (!perNoteMap[normalizedNoteKey])
                        perNoteMap[normalizedNoteKey] = [];

                    perNoteMap[normalizedNoteKey].push(entry);

                    source.onended = () => {
                        this._removeScheduledSourceEntry(componentId, entry);
                    };
                }

                const delayMs = Math.max(0, (when - ctx.currentTime) * 1000.0);
                const timeoutId = window.setTimeout(() => {
                    this._setNotePlaying(componentId, action.noteKey, action.isNoteOn);
                }, delayMs);

                timers.push(timeoutId);
            }

            state.nextActionIndex++;
        }
    },

    updateMidiTempo: async function (componentId, bpm) {
        const state = this._midiScheduleStateByComponent[componentId];
        if (!state || !state.isRunning)
            return;

        const numericBpm = Number(bpm);
        if (!Number.isFinite(numericBpm) || numericBpm <= 0)
            return;

        const ctx = await this._resumeAudioContext();
        const now = ctx.currentTime;

        const currentBeat = this._getBeatAtAudioTime(state, now);

        state.anchorAudioTime = now;
        state.anchorBeat = currentBeat;
        state.currentBpm = numericBpm;
    },

    playMetronomeSchedule: async function (actions, startAt) {
        if (!Array.isArray(actions) || actions.length === 0)
            return null;

        const ctx = await this._resumeAudioContext();
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
                if (index >= 0)
                    this._metronomeNodes.splice(index, 1);
            };
        }

        return actualStartAt;
    },

    playMetronomeTick: async function (isAccent, when) {
        const ctx = await this._resumeAudioContext();
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
            if (index >= 0)
                this._metronomeNodes.splice(index, 1);
        };
    },

    clearMidiScheduleState: function (componentId) {
        const clearOne = (id) => {
            const state = this._midiScheduleStateByComponent[id];

            if (state?.schedulerTimerId != null) {
                window.clearInterval(state.schedulerTimerId);
                state.schedulerTimerId = null;
            }

            delete this._midiScheduleStateByComponent[id];
            delete this._scheduledSourcesByComponent[id];
            delete this._scheduledSourcesByNoteByComponent[id];
            delete this._scheduledVisualTimersByComponent[id];

            this.clearPlayingVisuals(id);
        };

        if (!componentId) {
            for (const id of Object.keys(this._midiScheduleStateByComponent))
                clearOne(id);

            for (const id of Object.keys(this._scheduledSourcesByComponent))
                clearOne(id);

            return;
        }

        clearOne(componentId);
    },

    stopMidiSchedule: function (componentId) {
        if (!componentId) {
            for (const id of Object.keys(this._scheduledSourcesByComponent))
                this.stopMidiSchedule(id);

            for (const id of Object.keys(this._midiScheduleStateByComponent))
                this.clearMidiScheduleState(id);

            return;
        }

        const sources = this._scheduledSourcesByComponent[componentId] || [];
        const timers = this._scheduledVisualTimersByComponent[componentId] || [];
        const state = this._midiScheduleStateByComponent[componentId];

        if (state?.schedulerTimerId != null) {
            window.clearInterval(state.schedulerTimerId);
            state.schedulerTimerId = null;
        }

        if (state)
            state.isRunning = false;

        for (const entry of sources) {
            try {
                entry.source.stop();
            } catch { }
        }

        for (const timerId of timers)
            window.clearTimeout(timerId);

        this.clearMidiScheduleState(componentId);
    },

    stopMetronome: function () {
        for (const node of this._metronomeNodes) {
            try {
                node.stop();
            } catch { }
        }

        this._metronomeNodes = [];
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