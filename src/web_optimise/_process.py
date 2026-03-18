"""ffmpeg execution with progress tracking."""

import subprocess
import threading
from typing import TYPE_CHECKING

from web_optimise._constants import MICROSECONDS_PER_SECOND
from web_optimise._discovery import find_existing_output
from web_optimise._discovery import sanitise_filename
from web_optimise._modes import MODE_CONFIGS
from web_optimise._types import EncodeError
from web_optimise._types import EncodeResult

if TYPE_CHECKING:
    from pathlib import Path

    from rich.progress import Progress
    from rich.progress import TaskID

    from web_optimise._constants import Mode
    from web_optimise._types import FileInfo


def process_file(
    info: FileInfo,
    output_dir: Path,
    /,
    *,
    mode: Mode,
    overwrite: bool,
    progress: Progress,
    task_id: TaskID,
) -> EncodeResult:
    """
    Process a single file for web delivery.

    Dispatches to the command builder and verifier registered in
    ``MODE_CONFIGS`` for the given *mode* (remux, encode, or webm).

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
    config = MODE_CONFIGS[mode]
    existing = find_existing_output(
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
    output_path = output_dir / sanitise_filename(
        info.path.name,
        ext=config.output_ext,
    )
    cmd = config.cmd_builder(info.path, output_path)

    proc = subprocess.Popen(  # noqa: S603
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
        _ = proc.wait()
        stderr_thread.join()
    except Exception:
        proc.kill()
        _ = proc.wait()
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
    task_id: TaskID,
) -> None:
    """Parse ffmpeg -progress pipe:1 output and update the progress bar."""
    if proc.stdout is None:
        return
    for raw_line in proc.stdout:
        stripped = raw_line.strip()
        if stripped.startswith("out_time_us="):
            value = stripped.split("=", 1)[1]
            if value != "N/A":
                elapsed = int(value) / MICROSECONDS_PER_SECOND
                progress.update(task_id, completed=elapsed)
        elif stripped == "progress=end":
            progress.update(task_id, completed=total_duration)
