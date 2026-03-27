window.steelPan = {
    _refs: {},
    _audioCache: {},

    register: function (id, dotNetRef) {
        this._refs[id] = dotNetRef;
    },

    unregister: function (id) {
        delete this._refs[id];
    },

    preloadNotes: function (noteKeys) {
        if (!Array.isArray(noteKeys))
            return;

        for (const noteKey of noteKeys) {
            const path = this._getSamplePath(noteKey);

            if (this._audioCache[path])
                continue;

            const audio = new Audio(path);
            audio.preload = "auto";
            audio.load();
            this._audioCache[path] = audio;
        }
    },

    playNote: function (noteKey) {
        const path = this._getSamplePath(noteKey);

        let audio = this._audioCache[path];
        if (!audio) {
            audio = new Audio(path);
            audio.preload = "auto";
            this._audioCache[path] = audio;
        }

        const instance = audio.cloneNode();
        instance.currentTime = 0;
        instance.play().catch(() => {
        });
    },

    playNotes: function (noteKeys) {
        if (!Array.isArray(noteKeys))
            return;

        for (const noteKey of noteKeys) {
            this.playNote(noteKey);
        }
    },

    _getSamplePath: function (noteKey) {
        const normalized = this._normalizeEnharmonic(noteKey);
        return `/samples/${encodeURIComponent(normalized)}.wav`;
    },

    _normalizeEnharmonic: function (noteKey) {
        const match = /^([A-G])([#b]?)(\d+)$/.exec(noteKey);
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
    }
};

window.steelPanNoteClick = function (element, componentId, noteKey, event) {
    event.stopPropagation();

    const ref = window.steelPan._refs[componentId];
    if (!ref)
        return;

    const shift = !!event.shiftKey;

    if (!shift && !element.classList.contains("sp-note--active")) {
        window.steelPan.playNote(noteKey);

        element.classList.add("sp-note--flash");

        setTimeout(() => {
            element.classList.remove("sp-note--flash");
        }, 120);
    }

    ref.invokeMethodAsync("HandleNoteInteraction", noteKey, shift);
};