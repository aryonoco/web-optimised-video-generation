#!/usr/bin/env -S uv run --quiet --script
# /// script
# requires-python = ">=3.13,<3.15"
# dependencies = [
#     "rich>=14.0",
# ]
# ///
"""
Optimise media files for progressive web delivery.

Default "remux" mode applies container-level optimisations without
re-encoding: faststart (moov before mdat), metadata/chapter stripping,
and extra stream removal.  This is lossless and near-instant.

Optional "encode" mode re-encodes video to H.264 High profile with
2-second keyframes for responsive seeking via media-chrome.  Audio is
stream-copied unchanged.

MKV files containing AV1 video and Opus audio are automatically remuxed
to streaming-optimised WebM with cues-to-front, 2-second cluster
alignment, and metadata stripping.

Output files are written to a 'web-optimised' subdirectory.

Prerequisites:
    - Requires: ffmpeg, ffprobe (libx264 needed only for --mode encode)

Usage:
    ./web_optimise.py video.mp4                     # remux (default)
    ./web_optimise.py --mode encode video.mp4       # full re-encode
    ./web_optimise.py video.mkv                     # MKV→WebM (auto)
    ./web_optimise.py /path/to/directory/
    ./web_optimise.py --dry-run dir/
"""

import argparse
import contextlib
import json
import re
import subprocess
import sys
import threading
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Final

from rich.console import Console
from rich.progress import BarColumn
from rich.progress import Progress
from rich.progress import SpinnerColumn
from rich.progress import TaskProgressColumn
from rich.progress import TextColumn
from rich.progress import TimeElapsedColumn
from rich.progress import TimeRemainingColumn
from rich.table import Table

# ══════════════════════════════════════════════════════════════════════════════
#  CONSTANTS
# ══════════════════════════════════════════════════════════════════════════════

SUPPORTED_EXTENSIONS: Final[frozenset[str]] = frozenset({".mp4", ".m4v", ".mov", ".mkv"})
OUTPUT_DIR_NAME: Final[str] = "web-optimised"

# Processing modes
MODE_REMUX: Final[str] = "remux"
MODE_ENCODE: Final[str] = "encode"
MODE_WEBM: Final[str] = "webm"

_MKV_EXTENSIONS: Final[frozenset[str]] = frozenset({".mkv"})

# x264 encoding settings
CRF: Final[int] = 25
PRESET: Final[str] = "slower"
PROFILE: Final[str] = "high"
LEVEL: Final[str] = "4.0"
TARGET_FPS: Final[int] = 16
KEYFRAME_INTERVAL_SECS: Final[int] = 2
B_FRAMES: Final[int] = 3
X264_PARAMS: Final[str] = "deblock=-1,-1"

# Resolution thresholds for human-readable labels
_WIDTH_4K: Final[int] = 3840
_WIDTH_1440P: Final[int] = 2560
_WIDTH_1080P: Final[int] = 1920
_WIDTH_720P: Final[int] = 1280

# Verification thresholds
_MIN_KEYFRAMES_FOR_CHECK: Final[int] = 2
_MAX_KEYFRAME_SAMPLE: Final[int] = 10
_MAX_ACCEPTABLE_KEYFRAME_INTERVAL: Final[float] = 3.0
_MAX_WEB_FRAME_RATE: Final[int] = 30

# EBML element IDs for WebM verification
_EBML_CUES_ID: Final[bytes] = b"\x1c\x53\xbb\x6b"
_EBML_CLUSTER_ID: Final[bytes] = b"\x1f\x43\xb6\x75"
_EBML_SEEKHEAD_SKIP: Final[int] = 200
_WEBM_HEADER_READ_SIZE: Final[int] = 8192

# Unit conversion
_BYTES_PER_MB: Final[int] = 1024 * 1024
_MICROSECONDS_PER_SECOND: Final[int] = 1_000_000
_SECONDS_PER_HOUR: Final[int] = 3600
_SECONDS_PER_MINUTE: Final[int] = 60
_BITS_PER_KBIT: Final[int] = 1000

console: Final[Console] = Console()

# ══════════════════════════════════════════════════════════════════════════════
#  TYPE ALIASES
# ══════════════════════════════════════════════════════════════════════════════

type CmdBuilder = Callable[[Path, Path], tuple[str, ...]]
type Verifier = Callable[[Path], tuple[bool, list[str]]]

# ══════════════════════════════════════════════════════════════════════════════
#  EXCEPTIONS
# ══════════════════════════════════════════════════════════════════════════════


class WebOptimiseError(Exception):
    """Base exception for web optimisation errors."""


class ProbeError(WebOptimiseError):
    """ffprobe failed to analyse a file."""


class EncodeError(WebOptimiseError):
    """ffmpeg encoding failed."""

    def __init__(
        self,
        message: str,
        *,
        cmd: tuple[str, ...] | None = None,
    ) -> None:
        """Initialise with message and optional command that failed."""
        super().__init__(message)
        self.cmd = cmd


class ValidationError(WebOptimiseError):
    """Input validation failed."""


# ══════════════════════════════════════════════════════════════════════════════
#  DATA CLASSES
# ══════════════════════════════════════════════════════════════════════════════


@dataclass(frozen=True, slots=True, kw_only=True)
class VideoStream:
    """Immutable video stream metadata from ffprobe."""

    codec: str
    profile: str
    width: int
    height: int
    frame_rate: float
    bitrate: int | None = None

    @property
    def resolution_label(self) -> str:
        """Return human-readable resolution (e.g. '1080p')."""
        if self.width >= _WIDTH_4K:
            return "4K"
        if self.width >= _WIDTH_1440P:
            return "1440p"
        if self.width >= _WIDTH_1080P:
            return "1080p"
        if self.width >= _WIDTH_720P:
            return "720p"
        return f"{self.width}x{self.height}"

    @property
    def bitrate_kbps(self) -> str:
        """Return bitrate in kbps for display, or 'N/A'."""
        if self.bitrate is not None:
            return f"{self.bitrate // _BITS_PER_KBIT} kbps"
        return "N/A"


@dataclass(frozen=True, slots=True, kw_only=True)
class AudioStream:
    """Immutable audio stream metadata from ffprobe."""

    codec: str
    channels: int
    sample_rate: int
    bitrate: int | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class FileInfo:
    """Immutable media file metadata from ffprobe."""

    path: Path
    duration_secs: float
    size_bytes: int
    video: VideoStream
    audio: AudioStream | None = None

    @classmethod
    def from_ffprobe(cls, path: Path, data: dict[str, object]) -> "FileInfo":
        """
        Parse ffprobe JSON output into FileInfo.

        Args:
            path: Path to the media file.
            data: Parsed JSON dict from ffprobe -show_streams -show_format.

        Raises:
            ProbeError: If no video stream found or data is malformed.

        """
        streams = data.get("streams", [])
        if not isinstance(streams, list):
            msg = f"Unexpected streams type in {path}"
            raise ProbeError(msg)

        video_stream = _find_first_stream(streams, "video")
        audio_stream_data = _find_first_stream(streams, "audio")

        if video_stream is None:
            msg = f"No video stream found in {path}"
            raise ProbeError(msg)

        frame_rate = _parse_frame_rate(
            video_stream.get("r_frame_rate", "30/1"),
        )

        video = VideoStream(
            codec=video_stream.get("codec_name", "unknown"),
            profile=video_stream.get("profile", "unknown"),
            width=video_stream.get("width", 0),
            height=video_stream.get("height", 0),
            frame_rate=frame_rate,
            bitrate=_safe_int(video_stream.get("bit_rate")),
        )

        audio: AudioStream | None = None
        if audio_stream_data is not None:
            audio = AudioStream(
                codec=audio_stream_data.get("codec_name", "unknown"),
                channels=audio_stream_data.get("channels", 0),
                sample_rate=int(audio_stream_data.get("sample_rate", 0)),
                bitrate=_safe_int(audio_stream_data.get("bit_rate")),
            )

        fmt = data.get("format", {})
        if not isinstance(fmt, dict):
            fmt = {}

        return cls(
            path=path,
            duration_secs=float(fmt.get("duration", 0)),
            size_bytes=int(fmt.get("size", 0)),
            video=video,
            audio=audio,
        )

    @property
    def duration_display(self) -> str:
        """Return human-readable duration (e.g. '47m 41s')."""
        total = int(self.duration_secs)
        hours, remainder = divmod(total, _SECONDS_PER_HOUR)
        minutes, seconds = divmod(remainder, _SECONDS_PER_MINUTE)
        if hours > 0:
            return f"{hours}h {minutes:02d}m {seconds:02d}s"
        return f"{minutes}m {seconds:02d}s"

    @property
    def size_mb(self) -> float:
        """Return file size in megabytes."""
        return self.size_bytes / _BYTES_PER_MB


@dataclass(frozen=True, slots=True, kw_only=True)
class EncodeResult:
    """Immutable result of a single file encoding."""

    input_path: Path
    output_path: Path
    input_size: int
    output_size: int

    @property
    def savings_pct(self) -> float:
        """Return percentage change in file size (negative = smaller)."""
        if self.input_size == 0:
            return 0.0
        return ((self.output_size - self.input_size) / self.input_size) * 100

    @property
    def savings_display(self) -> str:
        """Return human-readable savings (e.g. '-23.4%' or '+5.2%')."""
        pct = self.savings_pct
        sign = "+" if pct > 0 else ""
        return f"{sign}{pct:.1f}%"


type ProcessResult = tuple[EncodeResult, str]

# ══════════════════════════════════════════════════════════════════════════════
#  PRIVATE HELPER FUNCTIONS
# ══════════════════════════════════════════════════════════════════════════════


def _find_first_stream(
    streams: list[dict[str, object]],
    codec_type: str,
) -> dict[str, object] | None:
    """Return the first stream matching the given codec_type, or None."""
    for stream in streams:
        if stream.get("codec_type") == codec_type:
            return stream
    return None


def _parse_frame_rate(fps_str: str) -> float:
    """
    Parse fractional frame rate string (e.g. '16/1' or '228865/14304').

    Returns:
        Frame rate as float, or 0.0 if unparseable.

    """
    parts = fps_str.split("/")
    num = int(parts[0]) if parts else 0
    den = int(parts[1]) if len(parts) > 1 else 1
    if den == 0:
        return 0.0
    return num / den


def _safe_int(value: object) -> int | None:
    """Convert a value to int if possible, otherwise return None."""
    if value is None:
        return None
    try:
        return int(value)
    except (ValueError, TypeError):
        return None


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


def _sanitise_filename(name: str, *, ext: str) -> str:
    """
    Produce a safe, lowercase, UUID-prefixed filename from an original name.

    Only ``[a-z0-9-]`` survive in the slug portion.  The output extension
    is determined by *ext*.

    Args:
        name: Original filename (used to derive the slug).
        ext: Output extension including the dot (e.g. ``".mp4"``, ``".webm"``).

    Example:
        ``_sanitise_filename("Session 1 … Recording.mp4", ext=".mp4")``
        → ``"a1b2c3d4-…-ef12_session-1-recording.mp4"``

    """
    return f"{uuid.uuid4()}_{_slugify(name)}{ext}"


def _find_existing_output(
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
    if not output_dir.exists():
        return None
    for candidate in output_dir.iterdir():
        if candidate.name.endswith(suffix) and candidate.is_file():
            return candidate
    return None


def _effective_mode(info: FileInfo, *, user_mode: str) -> str:
    """
    Determine the processing mode for a file based on its container type.

    MKV files always use MODE_WEBM (auto-detected).
    MP4/M4V/MOV files use the user-specified mode.
    """
    if info.path.suffix.lower() in _MKV_EXTENSIONS:
        return MODE_WEBM
    return user_mode


def _validate_mkv_codecs(infos: tuple[FileInfo, ...]) -> None:
    """
    Validate that MKV files contain AV1 video and Opus audio.

    WebM containers require AV1/VP8/VP9 video and Opus/Vorbis audio.
    This script requires AV1 + Opus specifically.

    Raises:
        ValidationError: If any MKV file has incompatible codecs.

    """
    errors: list[str] = []
    for info in infos:
        if info.path.suffix.lower() not in _MKV_EXTENSIONS:
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


# ══════════════════════════════════════════════════════════════════════════════
#  PUBLIC FUNCTIONS
# ══════════════════════════════════════════════════════════════════════════════


def validate_environment() -> None:
    """
    Verify ffmpeg and ffprobe are available on PATH.

    Raises:
        ValidationError: If either tool is missing or fails to run.

    """
    for tool in ("ffmpeg", "ffprobe"):
        try:
            subprocess.run(  # noqa: S603 — tool is from a hardcoded tuple
                [tool, "-version"],
                capture_output=True,
                text=True,
                check=True,
            )
        except FileNotFoundError:
            msg = f"{tool} not found in PATH"
            raise ValidationError(msg) from None
        except subprocess.CalledProcessError as err:
            msg = f"{tool} failed to run"
            raise ValidationError(msg) from err


def probe_file(path: Path, /) -> FileInfo:
    """
    Run ffprobe and return structured file information.

    Args:
        path: Path to the media file.

    Raises:
        ProbeError: If ffprobe fails or output cannot be parsed.

    """
    try:
        result = subprocess.run(  # noqa: S603 — cmd is hardcoded constants
            [  # noqa: S607 — ffprobe found via PATH by design
                "ffprobe",
                "-v",
                "quiet",
                "-print_format",
                "json",
                "-show_streams",
                "-show_format",
                str(path),
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        data = json.loads(result.stdout)
    except subprocess.CalledProcessError as err:
        msg = f"ffprobe failed for {path.name}: {err.stderr}"
        raise ProbeError(msg) from err
    except json.JSONDecodeError as err:
        msg = f"Failed to parse ffprobe output for {path.name}: {err}"
        raise ProbeError(msg) from err
    else:
        return FileInfo.from_ffprobe(path, data)


def build_ffmpeg_cmd(
    input_path: Path,
    output_path: Path,
    /,
) -> tuple[str, ...]:
    """
    Build the ffmpeg command for web-optimised encoding.

    Produce H.264 High profile, 2-second keyframes, CFR 16fps,
    stream-copied audio, faststart, stripped metadata.
    """
    gop_size = TARGET_FPS * KEYFRAME_INTERVAL_SECS
    return (
        "ffmpeg",
        "-hide_banner",
        "-y",
        "-i",
        str(input_path),
        # Video encoding
        "-c:v",
        "libx264",
        "-preset",
        PRESET,
        "-crf",
        str(CRF),
        "-profile:v",
        PROFILE,
        "-level:v",
        LEVEL,
        "-pix_fmt",
        "yuv420p",
        # Frame rate: convert VFR to CFR
        "-r",
        str(TARGET_FPS),
        "-fps_mode",
        "cfr",
        # GOP structure
        "-g",
        str(gop_size),
        "-keyint_min",
        str(TARGET_FPS),
        "-bf",
        str(B_FRAMES),
        # x264 tuning
        "-x264-params",
        X264_PARAMS,
        # Audio: stream copy
        "-c:a",
        "copy",
        # Stream selection
        "-map",
        "0:v:0",
        "-map",
        "0:a:0",
        # Container optimisation
        "-movflags",
        "+faststart",
        "-map_metadata",
        "-1",
        # Progress output on stdout
        "-progress",
        "pipe:1",
        str(output_path),
    )


def build_remux_cmd(
    input_path: Path,
    output_path: Path,
    /,
) -> tuple[str, ...]:
    """
    Build the ffmpeg command for container-level remux optimisation.

    Stream-copies all codecs, selects first video and audio streams,
    applies faststart, and strips metadata and chapters.
    """
    return (
        "ffmpeg",
        "-hide_banner",
        "-y",
        "-i",
        str(input_path),
        # Stream copy (no re-encoding)
        "-c:v",
        "copy",
        "-c:a",
        "copy",
        # Stream selection
        "-map",
        "0:v:0",
        "-map",
        "0:a:0",
        # Container optimisation
        "-movflags",
        "+faststart",
        "-map_metadata",
        "-1",
        "-map_chapters",
        "-1",
        # Progress output on stdout
        "-progress",
        "pipe:1",
        str(output_path),
    )


def build_webm_remux_cmd(
    input_path: Path,
    output_path: Path,
    /,
) -> tuple[str, ...]:
    """
    Build the ffmpeg command for MKV-to-WebM remux optimisation.

    Stream-copies AV1 video and Opus audio, selects first video and audio
    streams, places Cues (seek index) at front, aligns clusters to
    2-second keyframe boundaries, and strips metadata and chapters.
    """
    return (
        "ffmpeg",
        "-hide_banner",
        "-y",
        "-i",
        str(input_path),
        # Stream copy (no re-encoding)
        "-c:v",
        "copy",
        "-c:a",
        "copy",
        # Stream selection
        "-map",
        "0:v:0",
        "-map",
        "0:a:0",
        # WebM container format
        "-f",
        "webm",
        # Container optimisation
        "-cues_to_front",
        "true",
        "-cluster_time_limit",
        "2000",
        "-write_crc32",
        "false",
        # Strip metadata
        "-map_metadata",
        "-1",
        "-map_chapters",
        "-1",
        # Progress output on stdout
        "-progress",
        "pipe:1",
        str(output_path),
    )


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
        if not resolved.exists():
            msg = f"Path does not exist: {p}"
            raise ValidationError(msg)

        if resolved.is_dir():
            _collect_from_directory(resolved, found, seen)
        elif resolved.is_file():
            _collect_single_file(p, resolved, found, seen)

    return tuple(found)


def _collect_from_directory(
    directory: Path,
    found: list[Path],
    seen: set[Path],
) -> None:
    """Scan a directory for supported media files and append to found list."""
    for f in sorted(directory.iterdir(), key=lambda x: x.name.lower()):
        if (
            f.is_file()
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


def process_file(
    info: FileInfo,
    output_dir: Path,
    /,
    *,
    mode: str,
    overwrite: bool,
    progress: Progress,
    task_id: int,
) -> EncodeResult:
    """
    Process a single file for web delivery.

    Dispatches to the command builder and verifier registered in
    ``_MODE_CONFIGS`` for the given *mode* (remux, encode, or webm).

    Args:
        info: Probed file metadata.
        output_dir: Directory to write the output file.
        mode: Processing mode (MODE_REMUX, MODE_ENCODE, or MODE_WEBM).
        overwrite: If True, overwrite existing output files.
        progress: Rich Progress instance for updating the progress bar.
        task_id: Rich task ID to update.

    Raises:
        EncodeError: If ffmpeg exits with a non-zero return code.

    """
    config = _MODE_CONFIGS[mode]
    existing = _find_existing_output(
        output_dir,
        info.path.name,
        ext=config.output_ext,
    )

    if existing is not None and not overwrite:
        progress.update(task_id, completed=info.duration_secs)
        return EncodeResult(
            input_path=info.path,
            output_path=existing,
            input_size=info.size_bytes,
            output_size=existing.stat().st_size,
        )
    if existing is not None:
        existing.unlink()

    output_dir.mkdir(parents=True, exist_ok=True)
    output_path = output_dir / _sanitise_filename(
        info.path.name,
        ext=config.output_ext,
    )
    cmd = config.cmd_builder(info.path, output_path)

    proc = subprocess.Popen(  # noqa: S603 — cmd is built from hardcoded constants
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    stderr_chunks: list[str] = []
    stderr_thread = threading.Thread(
        target=_drain_stderr,
        args=(proc, stderr_chunks),
        daemon=True,
    )
    stderr_thread.start()

    try:
        _read_ffmpeg_progress(proc, info.duration_secs, progress, task_id)
        proc.wait()
        stderr_thread.join()
    except Exception:
        proc.kill()
        proc.wait()
        stderr_thread.join()
        if output_path.exists():
            output_path.unlink()
        raise

    if proc.returncode != 0:
        stderr_output = "".join(stderr_chunks)
        if output_path.exists():
            output_path.unlink()
        msg = (
            f"{config.error_verb} failed for {info.path.name}:"
            f" exit code {proc.returncode}\n{stderr_output}"
        )
        raise EncodeError(msg, cmd=cmd)

    return EncodeResult(
        input_path=info.path,
        output_path=output_path,
        input_size=info.size_bytes,
        output_size=output_path.stat().st_size,
    )


def _drain_stderr(
    proc: subprocess.Popen[str],
    chunks: list[str],
) -> None:
    """Read stderr to completion so the pipe buffer never fills."""
    if proc.stderr is None:
        return
    chunks.extend(proc.stderr)


def _read_ffmpeg_progress(
    proc: subprocess.Popen[str],
    total_duration: float,
    progress: Progress,
    task_id: int,
) -> None:
    """Parse ffmpeg -progress pipe:1 output and update the progress bar."""
    if proc.stdout is None:
        return
    for raw_line in proc.stdout:
        stripped = raw_line.strip()
        if stripped.startswith("out_time_us="):
            value = stripped.split("=", 1)[1]
            if value != "N/A":
                elapsed = int(value) / _MICROSECONDS_PER_SECOND
                progress.update(task_id, completed=elapsed)
        elif stripped == "progress=end":
            progress.update(task_id, completed=total_duration)


def verify_output(path: Path, /) -> tuple[bool, list[str]]:
    """
    Verify output file has correct profile, faststart, and keyframes.

    Returns:
        Tuple of (all_ok, list_of_issues).

    """
    issues: list[str] = []
    _check_video_profile(path, issues)
    _check_faststart(path, issues)
    _check_keyframe_intervals(path, issues)
    return len(issues) == 0, issues


def verify_remux_output(path: Path, /) -> tuple[bool, list[str]]:
    """
    Verify remuxed output file has faststart applied.

    Profile and keyframe checks are skipped because remux does not
    alter the video stream.

    Returns:
        Tuple of (all_ok, list_of_issues).

    """
    issues: list[str] = []
    _check_faststart(path, issues)
    return len(issues) == 0, issues


def verify_webm_output(path: Path, /) -> tuple[bool, list[str]]:
    """
    Verify WebM output file has Cues element at front.

    Reads the file header and checks that the EBML Cues element appears
    before the first Cluster element, confirming the seek index is
    front-loaded for progressive web playback.

    Returns:
        Tuple of (all_ok, list_of_issues).

    """
    issues: list[str] = []
    _check_cues_front(path, issues)
    return len(issues) == 0, issues


def _check_cues_front(path: Path, issues: list[str]) -> None:
    """Verify Cues element appears before first Cluster in WebM file."""
    try:
        with path.open("rb") as f:
            header = f.read(_WEBM_HEADER_READ_SIZE)
    except OSError as err:
        issues.append(f"Could not read file for verification: {err}")
        return

    # Skip the SeekHead area to avoid matching the Cues reference
    # in the SeekHead rather than the actual Cues element.
    cues_pos = header.find(_EBML_CUES_ID, _EBML_SEEKHEAD_SKIP)
    cluster_pos = header.find(_EBML_CLUSTER_ID, _EBML_SEEKHEAD_SKIP)

    if cluster_pos == -1:
        issues.append("Could not find Cluster element in file header")
    elif cues_pos == -1:
        issues.append("Cues not at front: seek index is at end of file")
    elif cues_pos > cluster_pos:
        issues.append("Cues not at front: Cues element appears after first Cluster")


def _check_video_profile(path: Path, issues: list[str]) -> None:
    """Verify the output video stream uses H.264 High profile."""
    try:
        result = subprocess.run(  # noqa: S603 — cmd is hardcoded constants
            [  # noqa: S607 — ffprobe found via PATH by design
                "ffprobe",
                "-v",
                "quiet",
                "-print_format",
                "json",
                "-show_streams",
                str(path),
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        data = json.loads(result.stdout)
    except (subprocess.CalledProcessError, json.JSONDecodeError) as err:
        issues.append(f"ffprobe failed: {err}")
        return

    for stream in data.get("streams", []):
        if stream.get("codec_type") == "video":
            profile = stream.get("profile", "")
            if "High" not in profile:
                issues.append(f"Expected High profile, got '{profile}'")
            break


def _check_faststart(path: Path, issues: list[str]) -> None:
    """Verify moov atom appears before mdat (faststart)."""
    try:
        trace_result = subprocess.run(  # noqa: S603 — cmd is hardcoded constants
            ["ffprobe", "-v", "trace", str(path)],  # noqa: S607
            capture_output=True,
            text=True,
            check=False,
        )
    except subprocess.CalledProcessError:
        issues.append("Could not verify faststart")
        return

    stderr = trace_result.stderr
    moov_pos = stderr.find("type:'moov'")
    mdat_pos = stderr.find("type:'mdat'")
    if moov_pos == -1 or mdat_pos == -1:
        issues.append("Could not determine moov/mdat positions")
    elif moov_pos > mdat_pos:
        issues.append("faststart not applied: moov atom is after mdat")


def _check_keyframe_intervals(path: Path, issues: list[str]) -> None:
    """Verify keyframe intervals are approximately 2 seconds."""
    try:
        kf_result = subprocess.run(  # noqa: S603 — cmd is hardcoded constants
            [  # noqa: S607 — ffprobe found via PATH by design
                "ffprobe",
                "-v",
                "quiet",
                "-select_streams",
                "v:0",
                "-show_entries",
                "packet=pts_time,flags",
                "-of",
                "csv=p=0",
                str(path),
            ],
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError:
        issues.append("Could not verify keyframe intervals")
        return

    keyframe_times = _parse_keyframe_times(kf_result.stdout)

    if len(keyframe_times) >= _MIN_KEYFRAMES_FOR_CHECK:
        sample_count = min(
            len(keyframe_times) - 1,
            _MAX_KEYFRAME_SAMPLE,
        )
        intervals = [keyframe_times[i + 1] - keyframe_times[i] for i in range(sample_count)]
        avg_interval = sum(intervals) / len(intervals)
        if avg_interval > _MAX_ACCEPTABLE_KEYFRAME_INTERVAL:
            issues.append(
                f"Keyframe interval too large: {avg_interval:.1f}s "
                f"(expected ~{KEYFRAME_INTERVAL_SECS}s)"
            )


def _parse_keyframe_times(ffprobe_output: str) -> list[float]:
    """Extract keyframe timestamps from ffprobe packet output."""
    times: list[float] = []
    for raw_line in ffprobe_output.strip().split("\n"):
        if ",K" in raw_line:
            pts = raw_line.split(",")[0]
            with contextlib.suppress(ValueError):
                times.append(float(pts))
    return times


# ══════════════════════════════════════════════════════════════════════════════
#  MODE CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════


@dataclass(frozen=True, slots=True)
class ModeConfig:
    """Per-mode dispatch configuration."""

    cmd_builder: CmdBuilder
    verifier: Verifier
    output_ext: str
    error_verb: str
    label: str
    completion_verb: str


_MODE_CONFIGS: Final[dict[str, ModeConfig]] = {
    MODE_REMUX: ModeConfig(
        cmd_builder=build_remux_cmd,
        verifier=verify_remux_output,
        output_ext=".mp4",
        error_verb="Remuxing",
        label="remux",
        completion_verb="optimised",
    ),
    MODE_ENCODE: ModeConfig(
        cmd_builder=build_ffmpeg_cmd,
        verifier=verify_output,
        output_ext=".mp4",
        error_verb="Encoding",
        label="encode",
        completion_verb="encoded",
    ),
    MODE_WEBM: ModeConfig(
        cmd_builder=build_webm_remux_cmd,
        verifier=verify_webm_output,
        output_ext=".webm",
        error_verb="WebM remuxing",
        label="webm",
        completion_verb="optimised",
    ),
}


# ══════════════════════════════════════════════════════════════════════════════
#  DISPLAY AND SUMMARY
# ══════════════════════════════════════════════════════════════════════════════


def print_summary(results: tuple[EncodeResult, ...], /) -> None:
    """Print before/after file size comparison table."""
    table = Table(title="Encoding Results")
    table.add_column("File", style="cyan")
    table.add_column("Output", style="dim")
    table.add_column("Input Size", justify="right")
    table.add_column("Output Size", justify="right")
    table.add_column("Change", justify="right")

    total_input = 0
    total_output = 0

    for r in results:
        total_input += r.input_size
        total_output += r.output_size
        input_mb = r.input_size / _BYTES_PER_MB
        output_mb = r.output_size / _BYTES_PER_MB

        style = "green" if r.savings_pct < 0 else "red"
        table.add_row(
            r.input_path.name,
            r.output_path.name,
            f"{input_mb:.1f} MB",
            f"{output_mb:.1f} MB",
            f"[{style}]{r.savings_display}[/{style}]",
        )

    _add_totals_row(table, total_input, total_output)
    console.print(table)


def _add_totals_row(
    table: Table,
    total_input: int,
    total_output: int,
) -> None:
    """Add a bold totals row to the results table."""
    if total_input == 0:
        return
    total_input_mb = total_input / _BYTES_PER_MB
    total_output_mb = total_output / _BYTES_PER_MB
    total_pct = ((total_output - total_input) / total_input) * 100
    sign = "+" if total_pct > 0 else ""
    style = "green" if total_pct < 0 else "red"
    table.add_row(
        "[bold]Total[/bold]",
        "",
        f"[bold]{total_input_mb:.1f} MB[/bold]",
        f"[bold]{total_output_mb:.1f} MB[/bold]",
        f"[bold][{style}]{sign}{total_pct:.1f}%[/{style}][/bold]",
    )


def parse_args() -> argparse.Namespace:
    """Parse command-line arguments."""
    parser = argparse.ArgumentParser(
        description="Optimise media files for progressive web delivery.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "examples:\n"
            "    %(prog)s video.mp4"
            "                     Remux (default)\n"
            "    %(prog)s --mode encode video.mp4"
            "        Full re-encode\n"
            "    %(prog)s video.mkv"
            "                     MKV to WebM (auto)\n"
            "    %(prog)s /path/to/directory/"
            "           All media in directory\n"
            "    %(prog)s --dry-run dir/"
            "                Show what would be processed\n"
            "    %(prog)s --overwrite dir/"
            "              Re-process existing outputs\n"
        ),
    )
    parser.add_argument(
        "paths",
        nargs="+",
        type=Path,
        metavar="PATH",
        help="media file(s) or directory(ies) to process",
    )
    parser.add_argument(
        "--mode",
        choices=[MODE_REMUX, MODE_ENCODE],
        default=MODE_REMUX,
        help=(
            "processing mode for MP4/M4V/MOV files: 'remux' (default) "
            "applies container-level optimisations without re-encoding; "
            "'encode' fully re-encodes video to H.264 High profile with "
            "2-second keyframes. MKV files are always remuxed to WebM."
        ),
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="show what would be processed without encoding",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="re-process even if output file already exists",
    )
    return parser.parse_args()


def _display_analysis(
    infos: tuple[FileInfo, ...],
    *,
    user_mode: str,
) -> None:
    """Print the pre-encode file analysis table."""
    table = Table(title="File Analysis")
    table.add_column("File", style="cyan")
    table.add_column("Output Preview", style="dim")
    table.add_column("Duration", justify="right")
    table.add_column("Size", justify="right")
    table.add_column("Resolution")
    table.add_column("Codec")
    table.add_column("Video Bitrate", justify="right")

    for info in infos:
        file_mode = _effective_mode(info, user_mode=user_mode)
        config = _MODE_CONFIGS[file_mode]
        table.add_row(
            info.path.name,
            _sanitise_filename(info.path.name, ext=config.output_ext),
            info.duration_display,
            f"{info.size_mb:.1f} MB",
            info.video.resolution_label,
            f"{info.video.codec} ({info.video.profile})",
            info.video.bitrate_kbps,
        )

    console.print(table)


def _display_remux_warnings(infos: tuple[FileInfo, ...]) -> None:
    """Print advisory notes about MP4 files that may benefit from full encoding."""
    warnings: list[str] = []
    for info in infos:
        if info.path.suffix.lower() in _MKV_EXTENSIONS:
            continue
        name = info.path.name
        if info.video.codec != "h264":
            warnings.append(
                f"  [yellow]![/yellow] {name}: codec is {info.video.codec} "
                f"(not H.264). Use --mode encode to re-encode."
            )
        elif "High" not in info.video.profile:
            warnings.append(
                f"  [yellow]![/yellow] {name}: profile is {info.video.profile} "
                f"(not High). Use --mode encode to upgrade."
            )
        if info.video.frame_rate > _MAX_WEB_FRAME_RATE:
            warnings.append(
                f"  [yellow]![/yellow] {name}: frame rate is "
                f"{info.video.frame_rate:.0f}fps. "
                f"Use --mode encode to reduce to {TARGET_FPS}fps."
            )
    if warnings:
        console.print()
        for w in warnings:
            console.print(w)


def _process_all(
    infos: tuple[FileInfo, ...],
    *,
    mode: str,
    overwrite: bool,
) -> tuple[ProcessResult, ...]:
    """Process all files sequentially with a rich progress bar."""
    results: list[ProcessResult] = []

    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(),
        TaskProgressColumn(),
        TimeElapsedColumn(),
        TimeRemainingColumn(),
        console=console,
    ) as progress:
        for idx, info in enumerate(infos):
            output_dir = info.path.parent / OUTPUT_DIR_NAME
            file_mode = _effective_mode(info, user_mode=mode)
            description = f"[{idx + 1}/{len(infos)}] {info.path.name}"
            task_id = progress.add_task(
                description,
                total=info.duration_secs,
            )

            result = process_file(
                info,
                output_dir,
                mode=file_mode,
                overwrite=overwrite,
                progress=progress,
                task_id=task_id,
            )
            results.append((result, file_mode))

    return tuple(results)


def _verify_all(results: tuple[ProcessResult, ...]) -> bool:
    """Verify all output files and print results. Return True if all ok."""
    console.print("\n[bold]Verifying outputs...[/bold]")
    all_ok = True
    for result, file_mode in results:
        config = _MODE_CONFIGS[file_mode]
        ok, issues = config.verifier(result.output_path)
        if ok:
            console.print(f"  [green]✓[/green] {result.output_path.name}")
        else:
            all_ok = False
            console.print(f"  [red]✗[/red] {result.output_path.name}")
            for issue in issues:
                console.print(f"    [red]→[/red] {issue}")
    return all_ok


def main() -> None:
    """Script entry point."""
    try:
        args = parse_args()
        validate_environment()
        files = find_files(tuple(args.paths))

        if not files:
            console.print("[yellow]No supported media files found.[/yellow]")
            return

        # Reject --mode encode with MKV files
        mkv_files = [f for f in files if f.suffix.lower() in _MKV_EXTENSIONS]
        if args.mode == MODE_ENCODE and mkv_files:
            names = ", ".join(f.name for f in mkv_files)
            msg = f"--mode encode is not supported for MKV files: {names}"
            raise ValidationError(msg)

        infos = tuple(probe_file(path) for path in files)
        _validate_mkv_codecs(infos)

        effective_modes = {_effective_mode(info, user_mode=args.mode) for info in infos}
        mode_label = " + ".join(
            sorted(_MODE_CONFIGS[m].label for m in effective_modes),
        )
        console.print(
            f"\n[bold]Found {len(infos)} file(s) to process[/bold] (mode: {mode_label})\n",
        )
        _display_analysis(infos, user_mode=args.mode)

        if args.mode == MODE_REMUX:
            _display_remux_warnings(infos)

        if args.dry_run:
            dry_verbs = {_MODE_CONFIGS[m].completion_verb for m in effective_modes}
            dry_verb = dry_verbs.pop() if len(dry_verbs) == 1 else "processed"
            console.print(
                f"\n[yellow]Dry run — no files were {dry_verb}.[/yellow]",
            )
            return

        results_with_modes = _process_all(
            infos,
            mode=args.mode,
            overwrite=args.overwrite,
        )
        all_ok = _verify_all(results_with_modes)

        console.print()
        print_summary(tuple(r for r, _ in results_with_modes))

        if all_ok:
            n = len(results_with_modes)
            verbs = {_MODE_CONFIGS[m].completion_verb for m in effective_modes}
            verb = verbs.pop() if len(verbs) == 1 else "processed"
            console.print(
                f"\n[bold green]Done![/bold green] {n} file(s) {verb}.",
            )
        else:
            console.print(
                "\n[bold yellow]Done with warnings.[/bold yellow] "
                "Check verification results above.",
            )

    except KeyboardInterrupt:
        console.print("\n[yellow]Interrupted by user[/yellow]")
        sys.exit(130)
    except WebOptimiseError as err:
        console.print(f"\n[red]Error:[/red] {err}")
        sys.exit(1)


if __name__ == "__main__":
    main()
