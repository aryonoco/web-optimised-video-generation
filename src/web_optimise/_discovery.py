"""File discovery, filename utilities, and mode resolution."""

import re
import uuid
from pathlib import Path

from web_optimise._constants import MKV_EXTENSIONS
from web_optimise._constants import OUTPUT_DIR_NAME
from web_optimise._constants import SUPPORTED_EXTENSIONS
from web_optimise._constants import Mode
from web_optimise._types import FileInfo
from web_optimise._types import ValidationError


def find_files(paths: tuple[Path, ...], /) -> tuple[Path, ...]:
    """
    Resolve CLI arguments to a deduplicated tuple of media file paths.

    Handles both individual files and directories.  Directories are scanned
    non-recursively for files with extensions in SUPPORTED_EXTENSIONS.
    Files already inside a directory named OUTPUT_DIR_NAME are skipped.

    Raises:
        ValidationError: If a path does not exist or has unsupported extension.

    """
    found: list[Path] = []
    seen: set[Path] = set()

    for p in paths:
        resolved = p.resolve()
        resolved_info = resolved.info
        if not resolved_info.exists():
            msg = f"Path does not exist: {p}"
            raise ValidationError(msg)

        if resolved_info.is_dir():
            _collect_from_directory(resolved, found, seen)
        elif resolved_info.is_file():
            _collect_single_file(p, resolved, found, seen)

    return tuple(found)


def effective_mode(info: FileInfo, *, user_mode: Mode) -> Mode:
    """
    Determine the processing mode for a file based on its container type.

    MKV files always use Mode.WEBM (auto-detected).
    MP4/M4V/MOV files use the user-specified mode.
    """
    if info.path.suffix.lower() in MKV_EXTENSIONS:
        return Mode.WEBM
    return user_mode


def validate_mkv_codecs(infos: tuple[FileInfo, ...]) -> None:
    """
    Validate that MKV files contain AV1 video and Opus audio.

    WebM containers require AV1/VP8/VP9 video and Opus/Vorbis audio.
    This script requires AV1 + Opus specifically.

    Raises:
        ValidationError: If any MKV file has incompatible codecs.

    """
    errors: list[str] = []
    for info in infos:
        if info.path.suffix.lower() not in MKV_EXTENSIONS:
            continue
        if info.video.codec != "av1":
            errors.append(
                f"{info.path.name}: video codec is {info.video.codec} (requires AV1 for WebM)"
            )
        if info.audio is None:
            errors.append(f"{info.path.name}: no audio stream found (requires Opus for WebM)")
        elif info.audio.codec != "opus":
            errors.append(
                f"{info.path.name}: audio codec is {info.audio.codec} (requires Opus for WebM)"
            )
    if errors:
        msg = "MKV codec requirements not met:\n" + "\n".join(f"  {e}" for e in errors)
        raise ValidationError(msg)


def sanitise_filename(name: str, *, ext: str) -> str:
    """
    Produce a safe, lowercase, UUID-prefixed filename from an original name.

    Only ``[a-z0-9-]`` survive in the slug portion.  The output extension
    is determined by *ext*.

    Args:
        name: Original filename (used to derive the slug).
        ext: Output extension including the dot (e.g. ``".mp4"``, ``".webm"``).

    """
    return f"{uuid.uuid4()}_{_slugify(name)}{ext}"


def find_existing_output(
    output_dir: Path,
    original_name: str,
    *,
    ext: str,
) -> Path | None:
    """
    Find an existing output file matching the sanitised slug of *original_name*.

    Scans *output_dir* for files whose name ends with ``_{slug}{ext}``.
    """
    suffix = f"_{_slugify(original_name)}{ext}"
    if not output_dir.info.exists():
        return None
    for candidate in output_dir.iterdir():
        if candidate.name.endswith(suffix) and candidate.info.is_file():
            return candidate
    return None


# ══════════════════════════════════════════════════════════════════════════════
#  PRIVATE HELPERS
# ══════════════════════════════════════════════════════════════════════════════


def _collect_from_directory(
    directory: Path,
    found: list[Path],
    seen: set[Path],
) -> None:
    """Scan a directory for supported media files and append to found list."""
    for f in sorted(directory.iterdir(), key=lambda x: x.name.lower()):
        if (
            f.info.is_file()
            and f.suffix.lower() in SUPPORTED_EXTENSIONS
            and f.parent.name != OUTPUT_DIR_NAME
            and f.resolve() not in seen
        ):
            seen.add(f.resolve())
            found.append(f)


def _collect_single_file(
    original_path: Path,
    resolved: Path,
    found: list[Path],
    seen: set[Path],
) -> None:
    """Validate and append a single file path to found list."""
    if resolved.suffix.lower() not in SUPPORTED_EXTENSIONS:
        msg = f"Unsupported file type '{resolved.suffix}': {original_path}"
        raise ValidationError(msg)
    if resolved not in seen:
        seen.add(resolved)
        found.append(resolved)


def _slugify(name: str) -> str:
    """
    Return a safe, lowercase slug from an original filename.

    Strips the extension, lowercases, replaces non-alphanumeric characters
    with hyphens, collapses runs of hyphens, and trims leading/trailing
    hyphens. Returns ``"unnamed"`` if nothing remains.
    """
    stem = Path(name).stem
    slug = re.sub(r"[^a-z0-9]+", "-", stem.lower()).strip("-")
    return slug or "unnamed"
