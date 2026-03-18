"""Output file verification for web-optimised media."""

import contextlib
import json
import subprocess
from typing import TYPE_CHECKING
from typing import Final

from web_optimise._constants import KEYFRAME_INTERVAL_SECS
from web_optimise._constants import MAX_ACCEPTABLE_KEYFRAME_INTERVAL
from web_optimise._constants import MAX_KEYFRAME_SAMPLE
from web_optimise._constants import MIN_KEYFRAMES_FOR_CHECK

if TYPE_CHECKING:
    from pathlib import Path


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


# ══════════════════════════════════════════════════════════════════════════════
#  PRIVATE CHECK FUNCTIONS
# ══════════════════════════════════════════════════════════════════════════════


# EBML element IDs for Matroska Level-1 elements.
_CUES_ELEMENT_ID: Final[bytes] = b"\x1c\x53\xbb\x6b"
_CLUSTER_ELEMENT_ID: Final[bytes] = b"\x1f\x43\xb6\x75"
_HEADER_SCAN_SIZE: Final[int] = 1024 * 1024  # 1 MB


def _ebml_vint_width(first_byte: int) -> int:
    """Return the byte-width of an EBML variable-length integer."""
    if first_byte == 0:
        return 0
    return 9 - first_byte.bit_length()


def _read_ebml_element_id(data: bytes, pos: int) -> tuple[bytes, int]:
    """Read an EBML element ID and return (id_bytes, new_position)."""
    if pos >= len(data):
        return b"", pos
    width = _ebml_vint_width(data[pos])
    end = pos + width
    if width == 0 or end > len(data):
        return b"", pos
    return data[pos:end], end


def _read_ebml_element_size(data: bytes, pos: int) -> tuple[int, int]:
    """Read an EBML element data size and return (size, new_position)."""
    if pos >= len(data):
        return -1, pos
    width = _ebml_vint_width(data[pos])
    end = pos + width
    if width == 0 or end > len(data):
        return -1, pos
    mask = (1 << (8 * width)) - 1
    value = int.from_bytes(data[pos:end]) & (mask >> width)
    return value, end


def _check_cues_front(path: Path, issues: list[str]) -> None:
    """
    Verify Cues element appears before first Cluster in WebM file.

    Parses EBML Level-1 element headers to determine the ordering of
    Cues and Cluster elements, rather than scanning for raw byte
    patterns which can produce false positives.
    """
    try:
        with path.open("rb") as f:
            data = f.read(_HEADER_SCAN_SIZE)
    except OSError as err:
        issues.append(f"Could not read file for verification: {err}")
        return

    pos = 0

    # Skip EBML header element (ID + size + data).
    elem_id, pos = _read_ebml_element_id(data, pos)
    size, pos = _read_ebml_element_size(data, pos)
    if not elem_id or size < 0:
        issues.append("Could not parse EBML header")
        return
    pos += size

    # Read Segment element header (ID + size); children follow immediately.
    elem_id, pos = _read_ebml_element_id(data, pos)
    _, pos = _read_ebml_element_size(data, pos)
    if not elem_id:
        issues.append("Could not parse Segment element")
        return

    # Walk Level-1 elements until we find Cues or Cluster.
    while pos < len(data):
        elem_id, id_end = _read_ebml_element_id(data, pos)
        if not elem_id:
            break
        size, data_start = _read_ebml_element_size(data, id_end)
        if size < 0:
            break

        if elem_id == _CUES_ELEMENT_ID:
            return  # Cues found before any Cluster — verification passed.
        if elem_id == _CLUSTER_ELEMENT_ID:
            issues.append("Cues not at front: first Cluster appears before Cues element")
            return

        pos = data_start + size

    issues.append("Could not locate Cues or Cluster element in file header")


def _check_video_profile(path: Path, issues: list[str]) -> None:
    """Verify the output video stream uses H.264 High profile."""
    try:
        result = subprocess.run(  # noqa: S603
            [
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
            if not isinstance(profile, str) or "High" not in profile:
                issues.append(f"Expected High profile, got '{profile}'")
            break


def _check_faststart(path: Path, issues: list[str]) -> None:
    """Verify moov atom appears before mdat (faststart)."""
    try:
        trace_result = subprocess.run(  # noqa: S603
            ["ffprobe", "-v", "trace", str(path)],
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
        kf_result = subprocess.run(  # noqa: S603
            [
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

    if len(keyframe_times) >= MIN_KEYFRAMES_FOR_CHECK:
        sample_count = min(
            len(keyframe_times) - 1,
            MAX_KEYFRAME_SAMPLE,
        )
        intervals = [keyframe_times[i + 1] - keyframe_times[i] for i in range(sample_count)]
        avg_interval = sum(intervals) / len(intervals)
        if avg_interval > MAX_ACCEPTABLE_KEYFRAME_INTERVAL:
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
