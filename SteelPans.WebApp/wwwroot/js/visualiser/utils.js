export function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

export function getAudioTimeOrNull() {
    const audioTime =
        window.panPlayback?.peekAudioTime?.() ??
        window.steelPan?.peekAudioTime?.() ??
        null;

    return Number.isFinite(audioTime) ? audioTime : null;
}

export function getCssIsolationAttribute(root) {
    for (const attribute of root.attributes) {
        if (/^b-[a-z0-9]+$/i.test(attribute.name))
            return attribute.name;
    }

    return null;
}

export function createScopedElement(state, tagName, className) {
    const element = document.createElement(tagName);

    if (className)
        element.className = className;

    if (state.scopeAttribute)
        element.setAttribute(state.scopeAttribute, "");

    return element;
}

export function getSecondsPerBeat(bpm, beatUnit) {
    const safeBpm = Math.max(Number(bpm) || 120, 1);
    const safeBeatUnit = Math.max(Number(beatUnit) || 4, 1);
    return (60.0 / safeBpm) * (4.0 / safeBeatUnit);
}

export function formatNoteFromSemitone(semitoneNumber) {
    const safe = Math.round(Number(semitoneNumber) || 0);
    const names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    const pitchClass = ((safe % 12) + 12) % 12;
    const octave = Math.floor(safe / 12);
    return `${names[pitchClass]}${octave}`;
}

export function formatTime(totalSeconds) {
    const safe = Math.max(0, Number(totalSeconds) || 0);
    const minutes = Math.floor(safe / 60);
    const seconds = Math.floor(safe % 60);
    const millis = Math.floor((safe - Math.floor(safe)) * 1000);
    return `${minutes}:${String(seconds).padStart(2, "0")}.${String(millis).padStart(3, "0")}`;
}
