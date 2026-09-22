from __future__ import annotations

import argparse
import base64
import binascii
import json
import os
import re
import secrets
import struct
from pathlib import Path

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError as exc:
    raise RuntimeError(
        "Install the 'cryptography' package before building audio packs: "
        "pip install cryptography"
    ) from exc

try:
    from dotenv import dotenv_values
except ImportError as exc:
    raise RuntimeError(
        "Install the 'python-dotenv' package before building audio packs: "
        "pip install python-dotenv"
    ) from exc


SCRIPT_DIR = Path(__file__).resolve().parent

PACK_KEY_ENV = "STEELPANS_AUDIO_PACK_KEY"
PACK_SOURCE_DIR_ENV = "PACKER_SOURCE_DIR"
PACK_OUTPUT_DIR_ENV = "PACKER_OUTPUT_DIR"
PACK_DEFINITION_FILE_ENV = "PACKER_DEFINITION_FILE"

AAD = b"SteelPans.AudioPack.v1"
NOTE_RE = re.compile(r"^([A-Ga-g])([#b]?)(-?\d+)$")
PACK_ID_RE = re.compile(r"^[A-Za-z0-9_-]+$")

SEMITONES = {
    "C": 0,
    "C#": 1,
    "DB": 1,
    "D": 2,
    "D#": 3,
    "EB": 3,
    "E": 4,
    "F": 5,
    "F#": 6,
    "GB": 6,
    "G": 7,
    "G#": 8,
    "AB": 8,
    "A": 9,
    "A#": 10,
    "BB": 10,
    "B": 11,
}

SHARP_NAMES = [
    "C",
    "C#",
    "D",
    "D#",
    "E",
    "F",
    "F#",
    "G",
    "G#",
    "A",
    "A#",
    "B",
]


def normalize_note(note: str) -> str:
    match = NOTE_RE.match(note)
    if not match:
        raise ValueError(
            f"Note must be in a format such as C4, C#4 or Eb4: {note}"
        )

    pitch = (match.group(1) + match.group(2)).upper()
    octave = int(match.group(3))
    semitone = SEMITONES[pitch]

    return f"{SHARP_NAMES[semitone]}{octave}"


def load_settings(env_file: Path) -> dict[str, str]:
    settings: dict[str, str] = {}

    if env_file.is_file():
        settings.update({
            key: value
            for key, value in dotenv_values(env_file).items()
            if value is not None
        })

    settings.update(os.environ)

    return settings


def require_setting(
    settings: dict[str, str],
    name: str,
) -> str:
    value = settings.get(name)

    if not value:
        raise RuntimeError(
            f"{name} is not set in the environment or .env file."
        )

    return value


def resolve_path(
    value: str,
    env_file: Path,
) -> Path:
    path = Path(value).expanduser()

    if not path.is_absolute():
        path = env_file.parent / path

    return path.resolve()


def load_key(
    settings: dict[str, str],
) -> bytes:
    encoded = require_setting(
        settings,
        PACK_KEY_ENV,
    )

    try:
        key = base64.b64decode(
            encoded,
            validate=True,
        )
    except (ValueError, binascii.Error) as exc:
        raise RuntimeError(
            f"{PACK_KEY_ENV} is not valid base64."
        ) from exc

    if len(key) != 32:
        raise RuntimeError(
            f"{PACK_KEY_ENV} must decode to exactly 32 bytes."
        )

    return key


def load_pack_definitions(
    path: Path,
) -> list[dict]:
    if not path.is_file():
        raise FileNotFoundError(
            f"Pack definition file does not exist: {path}"
        )

    try:
        definitions = json.loads(
            path.read_text(encoding="utf-8")
        )
    except json.JSONDecodeError as exc:
        raise RuntimeError(
            f"Invalid JSON in pack definition file: {path}"
        ) from exc

    if not isinstance(definitions, list):
        raise RuntimeError(
            f"Pack definition file must contain a JSON array: {path}"
        )

    seen_pack_ids: set[str] = set()

    for index, definition in enumerate(definitions):
        if not isinstance(definition, dict):
            raise RuntimeError(
                f"Pack definition at index {index} must be an object."
            )

        pack_id = definition.get("Pan")
        notes = definition.get("Notes")

        if not isinstance(pack_id, str) or not pack_id:
            raise RuntimeError(
                f"Pack definition at index {index} has an invalid Pan."
            )

        if not PACK_ID_RE.fullmatch(pack_id):
            raise ValueError(
                f"Invalid Pan id: {pack_id}"
            )

        if pack_id in seen_pack_ids:
            raise RuntimeError(
                f"Duplicate Pan in definition file: {pack_id}"
            )

        seen_pack_ids.add(pack_id)

        if not isinstance(notes, list) or not notes:
            raise RuntimeError(
                f"Pack definition for {pack_id} has no Notes."
            )

        seen_notes: set[str] = set()

        for note in notes:
            if not isinstance(note, str):
                raise RuntimeError(
                    f"Pack {pack_id} contains a non-string note."
                )

            normalized = normalize_note(note)

            if normalized in seen_notes:
                raise RuntimeError(
                    f"Pack {pack_id} contains duplicate/enharmonic duplicate "
                    f"note: {note} -> {normalized}"
                )

            seen_notes.add(normalized)

    return definitions


def build_sample_index(
    source_dir: Path,
) -> dict[str, Path]:
    if not source_dir.is_dir():
        raise FileNotFoundError(
            f"Pack source directory does not exist: {source_dir}"
        )

    sample_paths = sorted(
        (
            path
            for path in source_dir.iterdir()
            if path.is_file()
            and path.suffix.lower() == ".ogg"
        ),
        key=lambda path: path.name.lower(),
    )

    if not sample_paths:
        raise RuntimeError(
            f"No .ogg files found in: {source_dir}"
        )

    samples: dict[str, Path] = {}

    for path in sample_paths:
        note = normalize_note(path.stem)

        if note in samples:
            print(
                f"Skipping duplicate/enharmonic sample "
                f"{path.name}; using {samples[note].name} for {note}"
            )
            continue

        samples[note] = path

    return samples


def build_pack(
    definition: dict,
    source_samples: dict[str, Path],
    output_dir: Path,
    key: bytes,
) -> Path:
    pack_id: str = definition["Pan"]
    notes: list[str] = definition["Notes"]

    payload = bytearray()
    samples: dict[str, dict[str, int]] = {}

    for configured_note in notes:
        note = normalize_note(configured_note)
        path = source_samples.get(note)

        if path is None:
            raise FileNotFoundError(
                f"Missing sample for {pack_id}: "
                f"{configured_note} -> {note}"
            )

        data = path.read_bytes()

        samples[note] = {
            "offset": len(payload),
            "length": len(data),
        }

        payload.extend(data)

    manifest = json.dumps(
        {
            "formatVersion": 1,
            "packId": pack_id,
            "samples": samples,
        },
        separators=(",", ":"),
    ).encode("utf-8")

    plain = (
        b"SPP1"
        + struct.pack("<I", len(manifest))
        + manifest
        + payload
    )

    nonce = secrets.token_bytes(12)

    encrypted = AESGCM(key).encrypt(
        nonce,
        plain,
        AAD,
    )

    output = (
        b"SPE1"
        + nonce
        + encrypted
    )

    output_dir.mkdir(
        parents=True,
        exist_ok=True,
    )

    output_path = (
        output_dir
        / f"{pack_id}.spp"
    )

    output_path.write_bytes(output)

    print(
        f"Built {pack_id}: "
        f"{len(samples)} samples -> {output_path}"
    )

    return output_path


def make_packs(
    env_file: Path,
    pack_ids: list[str] | None = None,
) -> None:
    env_file = env_file.expanduser().resolve()

    settings = load_settings(
        env_file
    )

    key = load_key(
        settings
    )

    source_dir = resolve_path(
        require_setting(
            settings,
            PACK_SOURCE_DIR_ENV,
        ),
        env_file,
    )

    output_dir = resolve_path(
        require_setting(
            settings,
            PACK_OUTPUT_DIR_ENV,
        ),
        env_file,
    )

    definition_file = resolve_path(
        require_setting(
            settings,
            PACK_DEFINITION_FILE_ENV,
        ),
        env_file,
    )

    definitions = load_pack_definitions(
        definition_file
    )

    source_samples = build_sample_index(
        source_dir
    )

    definitions_by_pack = {
        definition["Pan"]: definition
        for definition in definitions
    }

    if pack_ids:
        missing_pack_ids = [
            pack_id
            for pack_id in pack_ids
            if pack_id not in definitions_by_pack
        ]

        if missing_pack_ids:
            raise RuntimeError(
                "Unknown pack(s): "
                + ", ".join(missing_pack_ids)
            )

        selected_definitions = [
            definitions_by_pack[pack_id]
            for pack_id in pack_ids
        ]
    else:
        selected_definitions = definitions

    if not selected_definitions:
        raise RuntimeError(
            f"No pack definitions found in: {definition_file}"
        )

    for definition in selected_definitions:
        build_pack(
            definition,
            source_samples,
            output_dir,
            key,
        )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build encrypted Steel Pans audio packs."
    )

    parser.add_argument(
        "-e",
        "--env",
        type=Path,
        default=SCRIPT_DIR / ".env",
        help=(
            "Path to the .env file. "
            "Defaults to .env in the script directory."
        ),
    )

    parser.add_argument(
        "pack_ids",
        nargs="*",
        help=(
            "Pan names to build. "
            "Builds all packs when omitted."
        ),
    )

    args = parser.parse_args()

    make_packs(
        args.env,
        args.pack_ids,
    )


if __name__ == "__main__":
    main()