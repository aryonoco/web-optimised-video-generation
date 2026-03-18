"""Rich console display functions for CLI output."""

from typing import TYPE_CHECKING
from typing import Final

from rich.console import Console
from rich.table import Table

from web_optimise._constants import BYTES_PER_MB
from web_optimise._constants import MKV_EXTENSIONS
from web_optimise._discovery import effective_mode
from web_optimise._discovery import sanitise_filename
from web_optimise._modes import MODE_CONFIGS

if TYPE_CHECKING:
    from web_optimise._constants import Mode
    from web_optimise._types import EncodeResult
    from web_optimise._types import FileInfo

console: Final[Console] = Console()


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


def display_analysis(
    infos: tuple[FileInfo, ...],
    *,
    user_mode: Mode,
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


def display_remux_warnings(infos: tuple[FileInfo, ...]) -> None:
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
    if warnings:
        console.print()
        for w in warnings:
            console.print(w)
