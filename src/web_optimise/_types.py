"""Domain types, exceptions, and data classes for web optimisation."""

from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import TypedDict
from typing import override

from web_optimise._constants import BITS_PER_KBIT
from web_optimise._constants import BYTES_PER_MB
from web_optimise._constants import SECONDS_PER_HOUR
from web_optimise._constants import SECONDS_PER_MINUTE
from web_optimise._constants import WIDTH_4K
from web_optimise._constants import WIDTH_720P
from web_optimise._constants import WIDTH_1080P
from web_optimise._constants import WIDTH_1440P
from web_optimise._constants import Mode

# ══════════════════════════════════════════════════════════════════════════════
#  TYPE ALIASES
# ══════════════════════════════════════════════════════════════════════════════

type CmdBuilder = Callable[[FileInfo, Path], tuple[str, ...]]
type Verifier = Callable[[Path], tuple[bool, list[str]]]


# ══════════════════════════════════════════════════════════════════════════════
#  FFPROBE JSON STRUCTURE (TypedDict for type-safe parsing boundary)
# ══════════════════════════════════════════════════════════════════════════════


class RawStream(TypedDict, total=False):
    """Typed representation of a single ffprobe stream entry."""

    codec_type: str
    codec_name: str
    profile: str
    width: int
    height: int
    r_frame_rate: str
    channels: int
    sample_rate: str
    bit_rate: str


class RawFormat(TypedDict, total=False):
    """Typed representation of the ffprobe format section."""

    duration: str
    size: str


class RawProbeData(TypedDict, total=False):
    """Typed representation of the full ffprobe JSON output."""

    streams: list[RawStream]
    format: RawFormat


# ══════════════════════════════════════════════════════════════════════════════
#  EXCEPTIONS
# ══════════════════════════════════════════════════════════════════════════════


class WebOptimiseError(Exception):
    """Base exception for web optimisation errors."""


class ProbeError(WebOptimiseError):
    """ffprobe failed to analyse a file."""


class EncodeError(WebOptimiseError):
    """ffmpeg encoding failed."""

    @override
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
#  PRIVATE HELPERS (used only by FileInfo.from_ffprobe)
# ══════════════════════════════════════════════════════════════════════════════


def _find_first_stream(
    streams: list[RawStream],
    codec_type: str,
) -> RawStream | None:
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


def _safe_int(value: str | None) -> int | None:
    """Convert a value to int if possible, otherwise return None."""
    if value is None:
        return None
    try:
        return int(value)
    except (ValueError, TypeError):
        return None


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
        if self.width >= WIDTH_4K:
            return "4K"
        if self.width >= WIDTH_1440P:
            return "1440p"
        if self.width >= WIDTH_1080P:
            return "1080p"
        if self.width >= WIDTH_720P:
            return "720p"
        return f"{self.width}x{self.height}"

    @property
    def bitrate_kbps(self) -> str:
        """Return bitrate in kbps for display, or 'N/A'."""
        if self.bitrate is not None:
            return f"{self.bitrate // BITS_PER_KBIT} kbps"
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
    def from_ffprobe(cls, path: Path, data: RawProbeData) -> FileInfo:
        """
        Parse ffprobe JSON output into FileInfo.

        Args:
            path: Path to the media file.
            data: Parsed JSON dict from ffprobe -show_streams -show_format.

        Raises:
            ProbeError: If no video stream found or data is malformed.

        """
        streams = data.get("streams", [])

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
                sample_rate=int(audio_stream_data.get("sample_rate", "0")),
                bitrate=_safe_int(audio_stream_data.get("bit_rate")),
            )

        fmt = data.get("format", {})

        return cls(
            path=path,
            duration_secs=float(fmt.get("duration", "0")),
            size_bytes=int(fmt.get("size", "0")),
            video=video,
            audio=audio,
        )

    @property
    def duration_display(self) -> str:
        """Return human-readable duration (e.g. '47m 41s')."""
        total = int(self.duration_secs)
        hours, remainder = divmod(total, SECONDS_PER_HOUR)
        minutes, seconds = divmod(remainder, SECONDS_PER_MINUTE)
        if hours > 0:
            return f"{hours}h {minutes:02d}m {seconds:02d}s"
        return f"{minutes}m {seconds:02d}s"

    @property
    def size_mb(self) -> float:
        """Return file size in megabytes."""
        return self.size_bytes / BYTES_PER_MB


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


type ProcessResult = tuple[EncodeResult, Mode]
