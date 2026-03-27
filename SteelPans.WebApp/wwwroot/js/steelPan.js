window.steelPan = {
    _refs: {},
    _clickTypes: {},
    _heldNotes: {},
    _audioCache: {},
    _globalPointerUpRegistered: false,

    register: function (id, dotNetRef) {
        this._refs[id] = dotNetRef;
        this._heldNotes[id] = this._heldNotes[id] || new Set();

        if (!this._globalPointerUpRegistered) {
            document.addEventListener("pointerup", this._handleGlobalPointerUp.bind(this));
            document.addEventListener("pointercancel", this._handleGlobalPointerUp.bind(this));
            this._globalPointerUpRegistered = true;
        }
    },

    unregister: function (id) {
        delete this._refs[id];
        delete this._clickTypes[id];
        delete this._heldNotes[id];
    },

    setClickType: function (id, clickType) {
        this._clickTypes[id] = clickType;
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
            // Ignore playback errors such as missing files or browser gesture issues.
        });
    },

    _getSamplePath: function (noteKey) {
        return `/samples/${noteKey}.wav`;
    },

    _handleGlobalPointerUp: function () {
        for (const componentId of Object.keys(this._heldNotes)) {
            const held = this._heldNotes[componentId];
            const ref = this._refs[componentId];

            if (!held || !ref || held.size === 0)
                continue;

            for (const noteKey of held) {
                ref.invokeMethodAsync("DeactivateNote", noteKey);
            }

            held.clear();
        }
    }
};

window.steelPanToggleClick = function (componentId, action, noteKey, event) {
    event.stopPropagation();

    if (window.steelPan._clickTypes[componentId] !== "Toggle")
        return;

    const ref = window.steelPan._refs[componentId];
    if (!ref)
        return;

    if (action === "activate") {
        ref.invokeMethodAsync("ActivateNote", noteKey);
    } else if (action === "deactivate") {
        ref.invokeMethodAsync("DeactivateNote", noteKey);
    }
};

window.steelPanHoldStart = function (componentId, noteKey, event) {
    event.stopPropagation();

    if (window.steelPan._clickTypes[componentId] !== "Hold")
        return;

    const ref = window.steelPan._refs[componentId];
    if (!ref)
        return;

    const held = window.steelPan._heldNotes[componentId] || new Set();
    window.steelPan._heldNotes[componentId] = held;

    if (held.has(noteKey))
        return;

    held.add(noteKey);
    ref.invokeMethodAsync("ActivateNote", noteKey);
};