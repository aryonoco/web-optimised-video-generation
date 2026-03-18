"""ffmpeg command builders for web-optimised encoding."""

from typing import TYPE_CHECKING

from web_optimise._constants import B_FRAMES
from web_optimise._constants import CRF
from web_optimise._constants import KEYFRAME_INTERVAL_SECS
from web_optimise._constants import LEVEL
from web_optimise._constants import PRESET
from web_optimise._constants import PROFILE
from web_optimise._constants import X264_PARAMS

if TYPE_CHECKING:
    from pathlib import Path

    from web_optimise._types import FileInfo


def build_ffmpeg_cmd(
    info: FileInfo,
    output_path: Path,
    /,
) -> tuple[str, ...]:
    """
    Build the ffmpeg command for web-optimised encoding.

    Produce H.264 High profile, 2-second keyframes,
    stream-copied audio, faststart, stripped metadata.
    """
    fps = max(round(info.video.frame_rate), 1)
    gop_size = fps * KEYFRAME_INTERVAL_SECS
    return (
        "ffmpeg",
        "-hide_banner",
        "-y",
        "-i",
        str(info.path),
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
        # GOP structure
        "-g",
        str(gop_size),
        "-keyint_min",
        str(fps),
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
    info: FileInfo,
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
        str(info.path),
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
    info: FileInfo,
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
        str(info.path),
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
