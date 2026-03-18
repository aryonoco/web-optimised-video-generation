"""Mode dispatch configuration for processing modes."""

from dataclasses import dataclass
from typing import TYPE_CHECKING
from typing import Final

from web_optimise._commands import build_ffmpeg_cmd
from web_optimise._commands import build_remux_cmd
from web_optimise._commands import build_webm_remux_cmd
from web_optimise._constants import MODE_ENCODE
from web_optimise._constants import MODE_REMUX
from web_optimise._constants import MODE_WEBM
from web_optimise._verify import verify_output
from web_optimise._verify import verify_remux_output
from web_optimise._verify import verify_webm_output

if TYPE_CHECKING:
    from web_optimise._types import CmdBuilder
    from web_optimise._types import Verifier


@dataclass(frozen=True, slots=True)
class ModeConfig:
    """Per-mode dispatch configuration."""

    cmd_builder: CmdBuilder
    verifier: Verifier
    output_ext: str
    error_verb: str
    label: str
    completion_verb: str


MODE_CONFIGS: Final[dict[str, ModeConfig]] = {
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
