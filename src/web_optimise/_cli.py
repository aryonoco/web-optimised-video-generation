"""CLI interface and main orchestration."""

import argparse
import sys
from pathlib import Path
from typing import TYPE_CHECKING

from rich.progress import BarColumn
from rich.progress import Progress
from rich.progress import SpinnerColumn
from rich.progress import TaskProgressColumn
from rich.progress import TextColumn
from rich.progress import TimeElapsedColumn
from rich.progress import TimeRemainingColumn

from web_optimise._constants import MKV_EXTENSIONS
from web_optimise._constants import OUTPUT_DIR_NAME
from web_optimise._constants import Mode
from web_optimise._discovery import effective_mode
from web_optimise._discovery import find_files
from web_optimise._discovery import validate_mkv_codecs
from web_optimise._display import console
from web_optimise._display import display_analysis
from web_optimise._display import display_remux_warnings
from web_optimise._display import print_summary
from web_optimise._modes import MODE_CONFIGS
from web_optimise._probe import probe_file
from web_optimise._probe import validate_environment
from web_optimise._process import process_file
from web_optimise._types import ValidationError
from web_optimise._types import WebOptimiseError

if TYPE_CHECKING:
    from web_optimise._types import FileInfo
    from web_optimise._types import ProcessResult


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
    _ = parser.add_argument(
        "paths",
        nargs="+",
        type=Path,
        metavar="PATH",
        help="media file(s) or directory(ies) to process",
    )
    _ = parser.add_argument(
        "--mode",
        choices=[Mode.REMUX, Mode.ENCODE],
        default=Mode.REMUX,
        help=(
            "processing mode for MP4/M4V/MOV files: 'remux' (default) "
            "applies container-level optimisations without re-encoding; "
            "'encode' fully re-encodes video to H.264 High profile with "
            "2-second keyframes. MKV files are always remuxed to WebM."
        ),
    )
    _ = parser.add_argument(
        "--dry-run",
        action="store_true",
        help="show what would be processed without encoding",
    )
    _ = parser.add_argument(
        "--overwrite",
        action="store_true",
        help="re-process even if output file already exists",
    )
    return parser.parse_args()


# ══════════════════════════════════════════════════════════════════════════════
#  ORCHESTRATION
# ══════════════════════════════════════════════════════════════════════════════


def _process_all(
    infos: tuple[FileInfo, ...],
    *,
    mode: Mode,
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
        if args.mode == Mode.ENCODE and mkv_files:
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
        display_analysis(infos, user_mode=args.mode)

        if args.mode == Mode.REMUX:
            display_remux_warnings(infos)

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
