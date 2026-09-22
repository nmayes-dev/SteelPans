from __future__ import annotations

import re
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

try:
    from dotenv import dotenv_values
except ImportError as exc:
    raise RuntimeError(
        "Install the 'python-dotenv' package: pip install python-dotenv"
    ) from exc


SCRIPT_DIR = Path(__file__).resolve().parent
ENV_FILE = SCRIPT_DIR / ".env"

SOURCE_DIR_ENV = "NOTES_SOURCE_DIR"
OUTPUT_DIR_ENV = "NOTES_OUTPUT_DIR"
MIN_NOTE_ENV = "NOTES_MIN_NOTE"
MAX_NOTE_ENV = "NOTES_MAX_NOTE"

TRIM_MS = 1800
FADE_IN_MS = 5
FADE_OUT_MS = 500
CLEAR_OUTPUT = False

OUTPUT_FORMAT = "ogg"  # wav, mp3, ogg, flac

MAX_DOWN_SHIFT = -5
MAX_UP_SHIFT = 7

HIGH_PASS_HZ = 35
LOW_PASS_BASE_HZ = 12000
LOW_PASS_DOWN_PER_SEMITONE_HZ = 450

NORMALIZE = True


PITCH_CLASS_TO_SEMITONE = {
    "C": 0, "C#": 1, "DB": 1,
    "D": 2, "D#": 3, "EB": 3,
    "E": 4,
    "F": 5, "F#": 6, "GB": 6,
    "G": 7, "G#": 8, "AB": 8,
    "A": 9, "A#": 10, "BB": 10,
    "B": 11,
}

SEMITONE_TO_PITCH_CLASS = {
    0: "C",
    1: "C#",
    2: "D",
    3: "D#",
    4: "E",
    5: "F",
    6: "F#",
    7: "G",
    8: "G#",
    9: "A",
    10: "A#",
    11: "B",
}

NOTE_RE = re.compile(
    r"^([A-Ga-g](?:#|b)?)(-?\d+)$"
)


@dataclass(frozen=True)
class SourceSample:
    note_name: str
    midi: int
    path: Path


def load_settings() -> dict[str, str]:
    if not ENV_FILE.is_file():
        raise FileNotFoundError(
            f"Environment file does not exist: {ENV_FILE}"
        )

    values = dotenv_values(ENV_FILE)

    return {
        key: value
        for key, value in values.items()
        if value is not None
    }


def require_setting(
    settings: dict[str, str],
    name: str,
) -> str:
    value = settings.get(name)

    if not value:
        raise RuntimeError(
            f"{name} is not set in environment file: {ENV_FILE}"
        )

    return value


def resolve_path(value: str) -> Path:
    path = Path(value).expanduser()

    if not path.is_absolute():
        path = ENV_FILE.parent / path

    return path.resolve()


def note_to_midi(note: str) -> int:
    match = NOTE_RE.match(note.strip())

    if not match:
        raise ValueError(
            f"Invalid note: {note}"
        )

    pitch_class = match.group(1).upper()
    octave = int(match.group(2))

    if pitch_class not in PITCH_CLASS_TO_SEMITONE:
        raise ValueError(
            f"Unsupported pitch class: {pitch_class}"
        )

    return (
        (octave + 1) * 12
        + PITCH_CLASS_TO_SEMITONE[pitch_class]
    )


def midi_to_note(midi: int) -> str:
    pitch_class = SEMITONE_TO_PITCH_CLASS[
        midi % 12
    ]

    octave = (midi // 12) - 1

    return f"{pitch_class}{octave}"


def discover_source_samples(
    source_dir: Path,
) -> list[SourceSample]:
    samples: list[SourceSample] = []

    if not source_dir.exists():
        raise FileNotFoundError(
            f"Input directory does not exist: "
            f"{source_dir}"
        )

    for path in source_dir.iterdir():
        if not path.is_file():
            continue

        if path.suffix.lower() not in {
            ".wav",
            ".mp3",
            ".flac",
            ".ogg",
            ".m4a",
        }:
            continue

        if not NOTE_RE.match(path.stem):
            continue

        note_name = path.stem

        samples.append(
            SourceSample(
                note_name=note_name,
                midi=note_to_midi(note_name),
                path=path,
            )
        )

    return sorted(
        samples,
        key=lambda sample: sample.midi,
    )


def find_nearest_source(
    target_midi: int,
    sources: list[SourceSample],
) -> SourceSample:
    usable_sources = [
        source
        for source in sources
        if (
            MAX_DOWN_SHIFT
            <= target_midi - source.midi
            <= MAX_UP_SHIFT
        )
    ]

    if usable_sources:
        sources = usable_sources

    return min(
        sources,
        key=lambda source: (
            abs(source.midi - target_midi),
            0 if source.midi >= target_midi else 1,
            source.midi,
        ),
    )


def validate(
    source_dir: Path,
) -> None:
    if shutil.which("ffmpeg") is None:
        raise RuntimeError(
            "ffmpeg was not found on PATH."
        )

    sources = discover_source_samples(
        source_dir
    )

    if not sources:
        raise RuntimeError(
            f"No note-named source files found in: "
            f"{source_dir}"
        )


def clear_output_dir(
    output_dir: Path,
) -> None:
    if not output_dir.exists():
        return

    for path in output_dir.iterdir():
        if (
            path.is_file()
            and path.suffix.lower()
            in {
                ".wav",
                ".mp3",
                ".ogg",
                ".flac",
            }
        ):
            path.unlink()


def build_ffmpeg_output_args(
    output_format: str,
) -> list[str]:
    match output_format.lower():
        case "wav":
            return [
                "-c:a",
                "pcm_s16le",
                "-ar",
                "44100",
            ]

        case "mp3":
            return [
                "-c:a",
                "libmp3lame",
                "-b:a",
                "192k",
                "-ar",
                "44100",
            ]

        case "ogg":
            return [
                "-c:a",
                "libvorbis",
                "-q:a",
                "5",
                "-ar",
                "44100",
            ]

        case "flac":
            return [
                "-c:a",
                "flac",
                "-ar",
                "44100",
            ]

        case _:
            raise ValueError(
                f"Unsupported OUTPUT_FORMAT: "
                f"{output_format}"
            )


def ffmpeg_generate_note(
    input_path: Path,
    output_path: Path,
    semitone_shift: int,
) -> None:
    output_path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    factor = 2 ** (
        semitone_shift / 12.0
    )

    fade_start = max(
        (TRIM_MS - FADE_OUT_MS) / 1000.0,
        0,
    )

    filters = [
        f"asetrate=44100*{factor:.12f}",
        "aresample=44100:resampler=soxr:precision=28",
        f"atrim=0:{TRIM_MS / 1000.0:.3f}",
        "asetpts=N/SR/TB",
        "dcshift=shift=0",
        f"highpass=f={HIGH_PASS_HZ}",
        (
            f"afade=t=in:st=0:"
            f"d={FADE_IN_MS / 1000.0:.3f}"
        ),
        (
            f"afade=t=out:"
            f"st={fade_start:.3f}:"
            f"d={FADE_OUT_MS / 1000.0:.3f}"
        ),
    ]

    if semitone_shift < 0:
        low_pass_hz = max(
            5000,
            (
                LOW_PASS_BASE_HZ
                + semitone_shift
                * LOW_PASS_DOWN_PER_SEMITONE_HZ
            ),
        )

        filters.append(
            f"lowpass=f={low_pass_hz:.0f}"
        )

    if NORMALIZE:
        filters.append(
            "loudnorm=I=-18:TP=-1.5:LRA=11"
        )

    filter_chain = ",".join(filters)

    cmd = [
        "ffmpeg",
        "-y",
        "-v",
        "warning",
        "-fflags",
        "+discardcorrupt",
        "-err_detect",
        "ignore_err",
        "-avoid_negative_ts",
        "make_zero",
        "-i",
        str(input_path),
        "-af",
        filter_chain,
        *build_ffmpeg_output_args(
            OUTPUT_FORMAT
        ),
        str(output_path),
    ]

    subprocess.run(
        cmd,
        check=True,
    )


def make_notes() -> None:
    settings = load_settings()

    source_dir = resolve_path(
        require_setting(
            settings,
            SOURCE_DIR_ENV,
        )
    )

    output_dir = resolve_path(
        require_setting(
            settings,
            OUTPUT_DIR_ENV,
        )
    )

    min_note = require_setting(
        settings,
        MIN_NOTE_ENV,
    )

    max_note = require_setting(
        settings,
        MAX_NOTE_ENV,
    )

    validate(source_dir)

    output_dir.mkdir(
        parents=True,
        exist_ok=True,
    )

    if CLEAR_OUTPUT:
        clear_output_dir(
            output_dir
        )

    sources = discover_source_samples(
        source_dir
    )

    min_midi = note_to_midi(
        min_note
    )

    max_midi = note_to_midi(
        max_note
    )

    if min_midi > max_midi:
        raise ValueError(
            f"Minimum note {min_note} is higher "
            f"than maximum note {max_note}."
        )

    print(
        f"Discovered {len(sources)} "
        f"source samples."
    )

    print(
        f"Generating range: "
        f"{min_note} -> {max_note}"
    )

    print(
        f"Output format: {OUTPUT_FORMAT}"
    )

    print(
        "Pitch method: "
        "asetrate + soxr + rumble/buzz filtering"
    )

    print(
        f"Max down shift: "
        f"{MAX_DOWN_SHIFT:+d} semitones"
    )

    print(
        f"Max up shift: "
        f"{MAX_UP_SHIFT:+d} semitones"
    )

    print(
        f"Output dir: {output_dir}"
    )

    print()

    for midi in range(
        min_midi,
        max_midi + 1,
    ):
        target_note = midi_to_note(
            midi
        )

        output_file = (
            output_dir
            / f"{target_note}.{OUTPUT_FORMAT}"
        )

        if output_file.exists():
            print(
                f"{target_note:<4} already exists, skipping"
            )
            continue

        source = find_nearest_source(
            midi,
            sources,
        )

        semitone_shift = (
            midi - source.midi
        )

        ffmpeg_generate_note(
            input_path=source.path,
            output_path=output_file,
            semitone_shift=semitone_shift,
        )

        print(
            f"{target_note:<4} <= "
            f"{source.path.name:<10} "
            f"shift "
            f"{semitone_shift:+d} semitones"
        )

    print()
    print("Done.")


if __name__ == "__main__":
    make_notes()