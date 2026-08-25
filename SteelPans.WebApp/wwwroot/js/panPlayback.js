window.panPlayback = {
    _refs: {},
    _samplePackByComponent: {},
    _audioBuffers: {},
    _audioSampleBytes: {},
    _audioPacks: {},
    _audioPackPromises: {},
    _audioContext: null,
    _masterCompressor: null,
    _masterGain: null,
    _gainByComponent: {},
    _volumeByComponent: {},
    _scheduledSourcesByComponent: {},
    _scheduledSourcesByNoteByComponent: {},
    _scheduledVisualTimersByComponent: {},
    _panElements: {},
    _metronomeNodes: [],
    _metronomeGeneration: 0,
    _metronomeScheduleState: null,
    _midiScheduleStateByComponent: {},
    _diagnosticsEnabled: false,
    _midiPlaybackState: {
        isPlaying: false,
        positionSeconds: 0,
        durationSeconds: 0,
        midiStartAt: null,
        audioAnchorTime: null,
        initialMidiBpm: 120,
        tempoBpm: 120
    },

    setDiagnosticsEnabled: function (enabled) {
        this._diagnosticsEnabled = enabled === true;
        if (this._diagnosticsEnabled)
            console.info("[panPlayback diagnostics] enabled");
    },

    _diag: function (...args) {
        if (this._diagnosticsEnabled)
            console.info("[panPlayback diagnostics]", ...args);
    },

    _diagSlow: function (startedAt, label, data, thresholdMs = 16) {
        if (!this._diagnosticsEnabled)
            return;

        const elapsedMs = performance.now() - startedAt;
        const payload = Object.assign({ elapsedMs: Math.round(elapsedMs * 100) / 100 }, data || {});

        if (elapsedMs >= thresholdMs)
            console.warn("[panPlayback diagnostics] slow " + label, payload);
        else
            console.info("[panPlayback diagnostics] " + label, payload);
    },

    setMidiPlaybackState: function (state) {
        const next = state || {};
        const durationSeconds = Number(next.durationSeconds);
        const positionSeconds = Number(next.positionSeconds);
        const midiStartAt = Number(next.midiStartAt);
        const audioAnchorTime = Number(next.audioAnchorTime);
        const initialMidiBpm = Number(next.initialMidiBpm);
        const tempoBpm = Number(next.tempoBpm);

        this._midiPlaybackState = {
            isPlaying: next.isPlaying === true,
            positionSeconds: Number.isFinite(positionSeconds) ? Math.max(0, positionSeconds) : 0,
            durationSeconds: Number.isFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0.01,
            midiStartAt: Number.isFinite(midiStartAt) ? midiStartAt : null,
            audioAnchorTime: Number.isFinite(audioAnchorTime) ? audioAnchorTime : null,
            initialMidiBpm: Number.isFinite(initialMidiBpm) && initialMidiBpm > 0 ? initialMidiBpm : 120,
            tempoBpm: Number.isFinite(tempoBpm) && tempoBpm > 0 ? tempoBpm : 120
        };

        window.dispatchEvent(new CustomEvent("panplayback:midiplaybackstatechanged", {
            detail: this.getMidiPlaybackState()
        }));
    },

    getMidiPlaybackState: function () {
        return { ...this._midiPlaybackState };
    },

    register: function (id, dotNetRef, samplePack) {
        this._refs[id] = dotNetRef;
        this.setComponentSamplePack(id, samplePack);
    },

    setComponentSamplePack: function (componentId, samplePack) {
        if (!componentId)
            throw new Error("A component ID is required when assigning an audio pack.");

        if (!samplePack)
            throw new Error(`No audio pack was provided for component ${componentId}.`);

        this._samplePackByComponent[componentId] = samplePack;
    },

    _getComponentSamplePack: function (componentId) {
        const samplePack = this._samplePackByComponent[componentId];
        if (!samplePack)
            throw new Error(`No audio pack is registered for component ${componentId}.`);

        return samplePack;
    },

    unregister: function (id) {
        delete this._refs[id];
        delete this._samplePackByComponent[id];
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

    hasUserGestureOccurred: function() {
        return navigator.userActivation?.hasBeenActive === true;
    },

    _ensureAudioContext: function () {
        if (!this._audioContext) {
            const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
            this._audioContext = new AudioContextCtor();

            this._masterCompressor = this._audioContext.createDynamicsCompressor();
            this._masterGain = this._audioContext.createGain();

            this._masterCompressor.threshold.value = -18;
            this._masterCompressor.knee.value = 12;
            this._masterCompressor.ratio.value = 10;
            this._masterCompressor.attack.value = 0.003;
            this._masterCompressor.release.value = 0.15;

            this._masterGain.gain.value = 0.7;

            this._masterCompressor.connect(this._masterGain);
            this._masterGain.connect(this._audioContext.destination);
        }

        return this._audioContext;
    },

    _resumeAudioContext: async function () {
        const ctx = this._ensureAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        return ctx;
    },

    _getOrCreateComponentGain: function (componentId) {
        const audioContext = this._ensureAudioContext();

        let gain = this._gainByComponent[componentId];
        if (!gain) {
            gain = audioContext.createGain();
            gain.gain.value = this._volumeByComponent[componentId] ?? 1.0;
            gain.connect(this._masterCompressor);
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

    _getSampleCacheKey: function (packId, noteKey) {
        return `${packId}:${this._normalizeEnharmonic(noteKey)}`;
    },

    _parseAudioPack: function (plainBytes) {
        const bytes = new Uint8Array(plainBytes);
        const magic = new TextDecoder().decode(bytes.slice(0, 4));
        if (magic !== "SPP1")
            throw new Error("Invalid audio pack payload header.");

        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        const manifestLength = view.getUint32(4, true);
        const manifestStart = 8;
        const manifestEnd = manifestStart + manifestLength;
        const manifest = JSON.parse(new TextDecoder().decode(bytes.slice(manifestStart, manifestEnd)));

        return {
            manifest: manifest,
            payload: bytes.slice(manifestEnd)
        };
    },

    _loadAudioPack: async function (packId) {
        if (this._audioPacks[packId])
            return this._audioPacks[packId];

        if (this._audioPackPromises[packId])
            return await this._audioPackPromises[packId];

        const promise = (async () => {
            const response = await fetch(`/api/audio-packs/${encodeURIComponent(packId)}`, {
                cache: "no-store"
            });
            if (!response.ok)
                throw new Error(`Failed to load audio pack ${packId}: ${response.status} ${response.statusText}`);

            const plainBytes = await response.arrayBuffer();
            const pack = this._parseAudioPack(plainBytes);
            this._audioPacks[packId] = pack;
            return pack;
        })();

        this._audioPackPromises[packId] = promise;

        try {
            return await promise;
        } finally {
            delete this._audioPackPromises[packId];
        }
    },

    _preloadSampleBytes: async function (packId, noteKey) {
        const normalized = this._normalizeEnharmonic(noteKey);
        const cacheKey = this._getSampleCacheKey(packId, normalized);

        let bytes = this._audioSampleBytes[cacheKey];
        if (bytes)
            return bytes;

        const pack = await this._loadAudioPack(packId);
        const sample = pack.manifest.samples?.[normalized];
        if (!sample)
            throw new Error(`Audio pack ${packId} does not contain sample ${normalized}.`);

        bytes = pack.payload.slice(sample.offset, sample.offset + sample.length).buffer;
        this._audioSampleBytes[cacheKey] = bytes;
        return bytes;
    },

    _loadBuffer: async function (packId, noteKey) {
        const ctx = this._ensureAudioContext();
        const cacheKey = this._getSampleCacheKey(packId, noteKey);

        let buffer = this._audioBuffers[cacheKey];
        if (buffer)
            return buffer;

        const bytes = await this._preloadSampleBytes(packId, noteKey);
        buffer = await ctx.decodeAudioData(bytes.slice(0));

        this._audioBuffers[cacheKey] = buffer;
        return buffer;
    },

    preloadNoteFiles: async function (packId, noteKeys) {
        const uniqueNoteKeys = [
            ...new Set(noteKeys.filter(x => typeof x === "string" && x.length > 0))
        ];

        await this._loadAudioPack(packId);
        await Promise.allSettled(uniqueNoteKeys.map(x => this._preloadSampleBytes(packId, x)));
    },

    unlockAudio: async function () {
        const ctx = this._ensureAudioContext();

        if (ctx.state === "suspended")
            await ctx.resume();

        return ctx;
    },

    _decodePreloadedBuffer: async function (packId, noteKey) {
        const ctx = await this.unlockAudio();
        const cacheKey = this._getSampleCacheKey(packId, noteKey);

        let buffer = this._audioBuffers[cacheKey];
        if (buffer)
            return buffer;

        const bytes = await this._preloadSampleBytes(packId, noteKey);

        // decodeAudioData detaches/consumes the ArrayBuffer in some implementations,
        // so pass a copy if you want to keep the cached bytes.
        buffer = await ctx.decodeAudioData(bytes.slice(0));

        this._audioBuffers[cacheKey] = buffer;
        return buffer;
    },

    preloadNotesAfterGesture: async function (packId, noteKeys) {
        await this.unlockAudio();

        const uniqueNoteKeys = [
            ...new Set(noteKeys.filter(x => typeof x === "string" && x.length > 0))
        ];

        await Promise.allSettled(uniqueNoteKeys.map(x => this._decodePreloadedBuffer(packId, x)));
    },

    playNote: async function (componentId, noteKey) {
        const packId = this._getComponentSamplePack(componentId);
        const startedAt = performance.now();
        const ctx = await this._resumeAudioContext();

        const loadStartedAt = performance.now();
        const buffer = await this._loadBuffer(packId, noteKey);
        this._diagSlow(loadStartedAt, "playNote load buffer", { componentId, noteKey }, 8);

        const source = ctx.createBufferSource();
        source.buffer = buffer;

        const componentGain = this._getOrCreateComponentGain(componentId);
        source.connect(componentGain);

        const audioStartAt = ctx.currentTime;
        source.start();

        this._diagSlow(startedAt, "playNote", { componentId, noteKey, audioStartAt }, 8);
        return true;
    },

    playNotes: async function (componentId, noteKeys) {
        const packId = this._getComponentSamplePack(componentId);
        if (!Array.isArray(noteKeys) || noteKeys.length === 0)
            return;

        const ctx = await this._resumeAudioContext();
        const componentGain = this._getOrCreateComponentGain(componentId);

        for (const noteKey of noteKeys) {
            const buffer = await this._loadBuffer(packId, noteKey);

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
            noteEl.classList.toggle("sp-app-svg-note--on", !!isPlaying);
        }

        for (const labelEl of targets.labelElements) {
            labelEl.classList.toggle("sp-app-svg-label--on", !!isPlaying);
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
                schedulerIntervalMs: 25,
                noteOffReleaseSeconds: 0.4,
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
        const pointerStartedAt = performance.now();
        if (event.pointerType === "mouse" && event.button !== 0)
            return;

        event.stopPropagation();
        event.preventDefault();

        const ref = this._refs[componentId];
        if (!ref)
            return;

        let noteAudioTime = null;
        try {
            const audioStartedAt = performance.now();
            const ctx = await this._resumeAudioContext();
            noteAudioTime = ctx.currentTime;
            this._diagSlow(audioStartedAt, "notePointerDown resume audio", { componentId, noteKey, noteAudioTime }, 8);
        } catch {
            noteAudioTime = null;
        }

        const playNoteStartedAt = performance.now();
        const playNotePromise = this.playNote(componentId, noteKey);

        // Do not await the Blazor callback before allowing audio playback to complete.
        // On dense overdub tracks the callback can trigger a large visualiser render,
        // which otherwise blocks the pointer-down path and makes live note playback feel late.
        const callbackStartedAt = performance.now();
        ref.invokeMethodAsync("OnNotePointerDown", noteKey, noteAudioTime)
            .then(() => this._diagSlow(callbackStartedAt, "notePointerDown Blazor callback", { componentId, noteKey, noteAudioTime }, 16))
            .catch(error => console.warn("Note callback failed", error));

        playNotePromise
            .then(() => {
                this._diagSlow(playNoteStartedAt, "notePointerDown playNote promise", { componentId, noteKey }, 8);
                this._setNotePlaying(componentId, noteKey, true);

                const timeoutId = window.setTimeout(() => {
                    this._setNotePlaying(componentId, noteKey, false);
                }, 120);

                this._ensureComponentScheduleState(componentId);
                this._scheduledVisualTimersByComponent[componentId].push(timeoutId);
            })
            .catch(error => console.warn("Note playback failed", error));

        this._diagSlow(pointerStartedAt, "notePointerDown sync path", { componentId, noteKey, noteAudioTime }, 8);
    },

    prepareMidiSchedule: async function (componentId, scheduledActions) {
        if (!componentId || !Array.isArray(scheduledActions) || scheduledActions.length === 0)
            return;

        const ctx = await this._resumeAudioContext();
        const samplePack = this._getComponentSamplePack(componentId);
        const noteKeys = [...new Set(
            scheduledActions
                .filter(action => action && action.isNoteOn === true && typeof action.noteKey === "string" && action.noteKey.length > 0)
                .map(action => action.noteKey))];

        await Promise.all(noteKeys.map(noteKey => this._loadBuffer(samplePack, noteKey)));
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
            ? Math.max(numericStartAt, ctx.currentTime)
            : ctx.currentTime;

        const state = this._getOrCreateMidiScheduleState(componentId);
        state.baseBpm = effectiveBaseBpm;
        state.currentBpm = effectiveCurrentBpm;
        state.nextActionIndex = 0;
        state.isRunning = true;

        state.anchorAudioTime = actualStartAt;
        state.anchorBeat = 0;

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
        const scheduleStartedAt = performance.now();
        let scheduledActionCount = 0;
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
                    const loadStartedAt = performance.now();
                    const buffer = await this._loadBuffer(this._getComponentSamplePack(componentId), action.noteKey);
                    this._diagSlow(loadStartedAt, "schedule load buffer", { componentId, noteKey: action.noteKey }, 8);
                    const source = ctx.createBufferSource();
                    const noteGain = ctx.createGain();

                    source.buffer = buffer;

                    noteGain.gain.setValueAtTime(1.0, Math.max(when, ctx.currentTime));

                    source.connect(noteGain);
                    noteGain.connect(componentGain);

                    source.start(Math.max(when, ctx.currentTime));

                    const normalizedNoteKey = this._normalizeEnharmonic(action.noteKey);

                    const entry = {
                        noteKey: normalizedNoteKey,
                        source: source,
                        gain: noteGain,
                        startTime: when,
                        stopped: false
                    };

                    sources.push(entry);

                    if (!perNoteMap[normalizedNoteKey])
                        perNoteMap[normalizedNoteKey] = [];

                    perNoteMap[normalizedNoteKey].push(entry);

                    source.onended = () => {
                        try {
                            noteGain.disconnect();
                        } catch { }

                        this._removeScheduledSourceEntry(componentId, entry);
                    };
                } else {
                    const normalizedNoteKey = this._normalizeEnharmonic(action.noteKey);
                    const entries = perNoteMap[normalizedNoteKey] || [];

                    for (const entry of [...entries]) {
                        if (entry.stopped)
                            continue;

                        entry.stopped = true;

                        const stopAt = Math.max(when, ctx.currentTime);
                        const releaseEnd = stopAt + state.noteOffReleaseSeconds;

                        try {
                            entry.gain.gain.cancelScheduledValues(stopAt);
                            entry.gain.gain.setValueAtTime(entry.gain.gain.value, stopAt);
                            entry.gain.gain.linearRampToValueAtTime(0.0001, releaseEnd);
                            entry.source.stop(releaseEnd);
                        } catch { }
                    }
                }

                scheduledActionCount++;
                const delayMs = Math.max(0, (when - ctx.currentTime) * 1000.0);
                const timeoutId = window.setTimeout(() => {
                    this._setNotePlaying(componentId, action.noteKey, action.isNoteOn);
                }, delayMs);

                timers.push(timeoutId);
            }

            state.nextActionIndex++;
        }

        this._diagSlow(
            scheduleStartedAt,
            "schedule MIDI window",
            {
                componentId,
                windowStart,
                windowEnd,
                scheduledActionCount,
                nextActionIndex: state.nextActionIndex,
                totalActions: state.actions.length
            },
            16);
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

    playMetronomeSchedule: async function (actions, startAt, scoreOffsetAtStartSeconds) {
        if (!Array.isArray(actions) || actions.length === 0)
            return null;

        const ctx = await this._resumeAudioContext();
        const generation = ++this._metronomeGeneration;
        const actualStartAt = startAt ?? (ctx.currentTime + 0.05);
        const numericScoreOffset = Number(scoreOffsetAtStartSeconds);
        const scheduleActions = actions
            .filter(action => action && typeof action.timeSeconds === "number")
            .map(action => ({
                timeSeconds: Number(action.timeSeconds),
                isAccent: action.isAccent === true,
                isSubdivision: action.isSubdivision === true
            }))
            .sort((a, b) => a.timeSeconds - b.timeSeconds);

        this._metronomeScheduleState = {
            generation,
            startAt: actualStartAt,
            scoreOffsetAtStartSeconds: Number.isFinite(numericScoreOffset) ? numericScoreOffset : 0,
            actions: scheduleActions,
            firstActionTimeSeconds: scheduleActions.length > 0 ? scheduleActions[0].timeSeconds : 0,
            lastActionIndex: -1,
            isRunning: true
        };

        for (let actionIndex = 0; actionIndex < scheduleActions.length; actionIndex++) {
            const action = scheduleActions[actionIndex];
            const when = actualStartAt + action.timeSeconds;

            const oscillator = ctx.createOscillator();
            const gain = ctx.createGain();

            const isAccent = action.isAccent === true;
            const isSubdivision = action.isSubdivision === true;

            oscillator.type = "square";
            oscillator.frequency.value = isAccent
                ? 1400
                : isSubdivision
                    ? 850
                    : 1000;

            const peakGain = isAccent
                ? 0.25
                : isSubdivision
                    ? 0.07
                    : 0.15;

            gain.gain.setValueAtTime(0.0001, when);
            gain.gain.exponentialRampToValueAtTime(peakGain, when + 0.002);
            gain.gain.exponentialRampToValueAtTime(0.0001, when + 0.06);

            oscillator.connect(gain);
            gain.connect(ctx.destination);

            const entry = { oscillator, generation, started: false };
            this._metronomeNodes.push(entry);

            const startNode = () => {
                if (entry.generation !== this._metronomeGeneration)
                    return;

                entry.started = true;

                if (this._metronomeScheduleState?.generation === generation)
                    this._metronomeScheduleState.lastActionIndex = actionIndex;

                oscillator.start(when);
                oscillator.stop(when + 0.07);
            };

            const delayMs = Math.max(0, (when - ctx.currentTime) * 1000);
            entry.timerId = window.setTimeout(startNode, delayMs);

            oscillator.onended = () => {
                const index = this._metronomeNodes.indexOf(entry);
                if (index >= 0)
                    this._metronomeNodes.splice(index, 1);
            };
        }

        return actualStartAt;
    },

    getMetronomeScheduleState: function () {
        const state = this._metronomeScheduleState;
        if (!state?.isRunning)
            return null;

        const ctx = this._ensureAudioContext();
        const audioTime = ctx.currentTime;
        const elapsedSeconds = audioTime - state.startAt;
        const scoreSeconds = state.scoreOffsetAtStartSeconds + elapsedSeconds;
        const lastAction = state.lastActionIndex >= 0
            ? state.actions[state.lastActionIndex]
            : null;

        return {
            isRunning: true,
            generation: state.generation,
            startAt: state.startAt,
            audioTime,
            elapsedSeconds,
            scoreSeconds,
            firstActionTimeSeconds: state.firstActionTimeSeconds ?? 0,
            lastActionIndex: state.lastActionIndex,
            lastAction
        };
    },

    playMetronomeCountIn: async function (countInCount, countInNoteDivision, bpm, beatsPerBar, beatUnit, startAt) {
        const count = Math.max(0, Math.floor(Number(countInCount) || 0));
        if (count <= 0)
            return null;

        const numericBpm = Number(bpm);
        const numericCountInNoteDivision = Number(countInNoteDivision);
        const numericBeatsPerBar = Number(beatsPerBar);
        const numericBeatUnit = Number(beatUnit);

        const effectiveBpm = Number.isFinite(numericBpm) && numericBpm > 0 ? numericBpm : 120;
        const effectiveCountInNoteDivision = Number.isFinite(numericCountInNoteDivision) && numericCountInNoteDivision > 0 ? numericCountInNoteDivision : 4;
        const effectiveBeatsPerBar = Number.isFinite(numericBeatsPerBar) && numericBeatsPerBar > 0 ? numericBeatsPerBar : 4;
        const effectiveBeatUnit = Number.isFinite(numericBeatUnit) && numericBeatUnit > 0 ? numericBeatUnit : 4;

        const secondsPerCountInNote = (60.0 / effectiveBpm) * (4.0 / effectiveCountInNoteDivision);
        const secondsPerBeat = (60.0 / effectiveBpm) * (4.0 / effectiveBeatUnit);
        const secondsPerBar = effectiveBeatsPerBar * secondsPerBeat;

        if (secondsPerCountInNote <= 0 || secondsPerBeat <= 0 || secondsPerBar <= 0)
            return null;

        const accentEpsilon = 0.000001;
        const beatEpsilon = 0.000001;

        const actions = [];
        for (let i = 0; i < count; i++) {
            const secondsFromPlaybackStart = (i - count) * secondsPerCountInNote;

            const barPhase = ((secondsFromPlaybackStart % secondsPerBar) + secondsPerBar) % secondsPerBar;
            const beatPhase = ((secondsFromPlaybackStart % secondsPerBeat) + secondsPerBeat) % secondsPerBeat;

            const isAccent = barPhase <= accentEpsilon || Math.abs(barPhase - secondsPerBar) <= accentEpsilon;
            const isBeat = beatPhase <= beatEpsilon || Math.abs(beatPhase - secondsPerBeat) <= beatEpsilon;

            actions.push({
                timeSeconds: i * secondsPerCountInNote,
                isAccent: isAccent,
                isSubdivision: !isBeat
            });
        }

        return await this.playMetronomeSchedule(actions, startAt);
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
        this._metronomeGeneration++;

        for (const entry of this._metronomeNodes) {
            const node = entry?.oscillator ?? entry;

            if (entry?.timerId != null)
                window.clearTimeout(entry.timerId);

            try {
                node.stop();
            } catch { }

            try {
                node.disconnect();
            } catch { }
        }

        this._metronomeNodes = [];
        this._metronomeScheduleState = null;
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
