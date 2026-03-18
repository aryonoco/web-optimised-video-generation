"""Optimise media files for progressive web delivery."""

from web_optimise._cli import main
from web_optimise._cli import parse_args
from web_optimise._cli import print_summary
from web_optimise._commands import build_ffmpeg_cmd
from web_optimise._commands import build_remux_cmd
from web_optimise._commands import build_webm_remux_cmd
from web_optimise._constants import Mode
from web_optimise._discovery import find_files
from web_optimise._modes import MODE_CONFIGS
from web_optimise._modes import ModeConfig
from web_optimise._probe import probe_file
from web_optimise._probe import validate_environment
from web_optimise._process import process_file
from web_optimise._types import AudioStream
from web_optimise._types import CmdBuilder
from web_optimise._types import EncodeError
from web_optimise._types import EncodeResult
from web_optimise._types import FileInfo
from web_optimise._types import ProbeError
from web_optimise._types import ProcessResult
from web_optimise._types import ValidationError
from web_optimise._types import Verifier
from web_optimise._types import VideoStream
from web_optimise._types import WebOptimiseError
from web_optimise._verify import verify_output
from web_optimise._verify import verify_remux_output
from web_optimise._verify import verify_webm_output

__all__ = [
    "MODE_CONFIGS",
    "AudioStream",
    "CmdBuilder",
    "EncodeError",
    "EncodeResult",
    "FileInfo",
    "Mode",
    "ModeConfig",
    "ProbeError",
    "ProcessResult",
    "ValidationError",
    "Verifier",
    "VideoStream",
    "WebOptimiseError",
    "build_ffmpeg_cmd",
    "build_remux_cmd",
    "build_webm_remux_cmd",
    "find_files",
    "main",
    "parse_args",
    "print_summary",
    "probe_file",
    "process_file",
    "validate_environment",
    "verify_output",
    "verify_remux_output",
    "verify_webm_output",
]
