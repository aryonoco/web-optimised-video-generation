"""Output file verification for web-optimised media."""

import contextlib
import json
import subprocess
from typing import TYPE_CHECKING

from web_optimise._constants import EBML_CLUSTER_ID
from web_optimise._constants import EBML_CUES_ID
from web_optimise._constants import EBML_SEEKHEAD_SKIP
from web_optimise._constants import KEYFRAME_INTERVAL_SECS
from web_optimise._constants import MAX_ACCEPTABLE_KEYFRAME_INTERVAL
from web_optimise._constants import MAX_KEYFRAME_SAMPLE
from web_optimise._constants import MIN_KEYFRAMES_FOR_CHECK
from web_optimise._constants import WEBM_HEADER_READ_SIZE

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


def _check_cues_front(path: Path, issues: list[str]) -> None:
    """Verify Cues element appears before first Cluster in WebM file."""
    try:
        with path.open("rb") as f:
            header = f.read(WEBM_HEADER_READ_SIZE)
    except OSError as err:
        issues.append(f"Could not read file for verification: {err}")
        return

    # Skip the SeekHead area to avoid matching the Cues reference
    # in the SeekHead rather than the actual Cues element.
    cues_pos = header.find(EBML_CUES_ID, EBML_SEEKHEAD_SKIP)
    cluster_pos = header.find(EBML_CLUSTER_ID, EBML_SEEKHEAD_SKIP)

    if cluster_pos == -1:
        issues.append("Could not find Cluster element in file header")
    elif cues_pos == -1:
        issues.append("Cues not at front: seek index is at end of file")
    elif cues_pos > cluster_pos:
        issues.append("Cues not at front: Cues element appears after first Cluster")


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
            if "High" not in profile:  # type: ignore[operator]
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
