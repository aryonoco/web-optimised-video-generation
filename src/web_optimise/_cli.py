"""CLI interface, display functions, and main orchestration."""

import argparse
import sys
from pathlib import Path
from typing import TYPE_CHECKING
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

from web_optimise._constants import BYTES_PER_MB
from web_optimise._constants import MAX_WEB_FRAME_RATE
from web_optimise._constants import MKV_EXTENSIONS
from web_optimise._constants import MODE_ENCODE
from web_optimise._constants import MODE_REMUX
from web_optimise._constants import OUTPUT_DIR_NAME
from web_optimise._constants import TARGET_FPS
from web_optimise._discovery import effective_mode
from web_optimise._discovery import find_files
from web_optimise._discovery import sanitise_filename
from web_optimise._discovery import validate_mkv_codecs
from web_optimise._modes import MODE_CONFIGS
from web_optimise._probe import probe_file
from web_optimise._probe import validate_environment
from web_optimise._process import process_file
from web_optimise._types import ValidationError
from web_optimise._types import WebOptimiseError

if TYPE_CHECKING:
    from web_optimise._types import EncodeResult
    from web_optimise._types import FileInfo
    from web_optimise._types import ProcessResult

console: Final[Console] = Console()


def parse_args() -> argparse.Namespace:
    """Parse command-line arguments."""
    parser = argparse.ArgumentParser(
        description="Optimise media files for progressive web delivery.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        suggest_on_error=True,
        color=True,
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


# ══════════════════════════════════════════════════════════════════════════════
#  DISPLAY FUNCTIONS
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
        input_mb = r.input_size / BYTES_PER_MB
        output_mb = r.output_size / BYTES_PER_MB

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
    total_input_mb = total_input / BYTES_PER_MB
    total_output_mb = total_output / BYTES_PER_MB
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
        file_mode = effective_mode(info, user_mode=user_mode)
        config = MODE_CONFIGS[file_mode]
        table.add_row(
            info.path.name,
            sanitise_filename(info.path.name, ext=config.output_ext),
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
        if info.path.suffix.lower() in MKV_EXTENSIONS:
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
        if info.video.frame_rate > MAX_WEB_FRAME_RATE:
            warnings.append(
                f"  [yellow]![/yellow] {name}: frame rate is "
                f"{info.video.frame_rate:.0f}fps. "
                f"Use --mode encode to reduce to {TARGET_FPS}fps."
            )
    if warnings:
        console.print()
        for w in warnings:
            console.print(w)


# ══════════════════════════════════════════════════════════════════════════════
#  ORCHESTRATION
# ══════════════════════════════════════════════════════════════════════════════


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
            file_mode = effective_mode(info, user_mode=mode)
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
        config = MODE_CONFIGS[file_mode]
        ok, issues = config.verifier(result.output_path)
        if ok:
            console.print(f"  [green]\u2713[/green] {result.output_path.name}")
        else:
            all_ok = False
            console.print(f"  [red]\u2717[/red] {result.output_path.name}")
            for issue in issues:
                console.print(f"    [red]\u2192[/red] {issue}")
    return all_ok


def main() -> None:
    """Entry point for the web-optimise CLI."""
    try:
        args = parse_args()
        validate_environment()
        files = find_files(tuple(args.paths))

        if not files:
            console.print("[yellow]No supported media files found.[/yellow]")
            return

        # Reject --mode encode with MKV files
        mkv_files = [f for f in files if f.suffix.lower() in MKV_EXTENSIONS]
        if args.mode == MODE_ENCODE and mkv_files:
            names = ", ".join(f.name for f in mkv_files)
            msg = f"--mode encode is not supported for MKV files: {names}"
            raise ValidationError(msg)

        infos = tuple(probe_file(path) for path in files)
        validate_mkv_codecs(infos)

        effective_modes = {effective_mode(info, user_mode=args.mode) for info in infos}
        mode_label = " + ".join(
            sorted(MODE_CONFIGS[m].label for m in effective_modes),
        )
        console.print(
            f"\n[bold]Found {len(infos)} file(s) to process[/bold] (mode: {mode_label})\n",
        )
        _display_analysis(infos, user_mode=args.mode)

        if args.mode == MODE_REMUX:
            _display_remux_warnings(infos)

        if args.dry_run:
            dry_verbs = {MODE_CONFIGS[m].completion_verb for m in effective_modes}
            dry_verb = dry_verbs.pop() if len(dry_verbs) == 1 else "processed"
            console.print(
                f"\n[yellow]Dry run \u2014 no files were {dry_verb}.[/yellow]",
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
            verbs = {MODE_CONFIGS[m].completion_verb for m in effective_modes}
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
