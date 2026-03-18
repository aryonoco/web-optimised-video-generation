"""ffprobe interaction for media file analysis."""

import json
import subprocess
from typing import TYPE_CHECKING

from web_optimise._types import FileInfo
from web_optimise._types import ProbeError
from web_optimise._types import ValidationError

if TYPE_CHECKING:
    from pathlib import Path


def validate_environment() -> None:
    """
    Verify ffmpeg and ffprobe are available on PATH.

    Raises:
        ValidationError: If either tool is missing or fails to run.

    """
    for tool in ("ffmpeg", "ffprobe"):
        try:
            subprocess.run(  # noqa: S603
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
        result = subprocess.run(  # noqa: S603
            [
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
