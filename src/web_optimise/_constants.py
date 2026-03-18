"""Constants for web-optimised video processing."""

import enum
from typing import Final

# ══════════════════════════════════════════════════════════════════════════════
#  FILE EXTENSIONS & OUTPUT
# ══════════════════════════════════════════════════════════════════════════════

SUPPORTED_EXTENSIONS: Final[frozenset[str]] = frozenset({".mp4", ".m4v", ".mov", ".mkv"})
OUTPUT_DIR_NAME: Final[str] = "web-optimised"

# ══════════════════════════════════════════════════════════════════════════════
#  PROCESSING MODES
# ══════════════════════════════════════════════════════════════════════════════


class Mode(enum.StrEnum):
    """Processing mode for media files."""

    REMUX = "remux"
    ENCODE = "encode"
    WEBM = "webm"


MKV_EXTENSIONS: Final[frozenset[str]] = frozenset({".mkv"})

# ══════════════════════════════════════════════════════════════════════════════
#  x264 ENCODING SETTINGS
# ══════════════════════════════════════════════════════════════════════════════

CRF: Final[int] = 25
PRESET: Final[str] = "slower"
PROFILE: Final[str] = "high"
LEVEL: Final[str] = "4.0"
KEYFRAME_INTERVAL_SECS: Final[int] = 2
B_FRAMES: Final[int] = 3
X264_PARAMS: Final[str] = "deblock=-1,-1"

# ══════════════════════════════════════════════════════════════════════════════
#  RESOLUTION THRESHOLDS
# ══════════════════════════════════════════════════════════════════════════════

WIDTH_4K: Final[int] = 3840
WIDTH_1440P: Final[int] = 2560
WIDTH_1080P: Final[int] = 1920
WIDTH_720P: Final[int] = 1280

# ══════════════════════════════════════════════════════════════════════════════
#  VERIFICATION THRESHOLDS
# ══════════════════════════════════════════════════════════════════════════════

MIN_KEYFRAMES_FOR_CHECK: Final[int] = 2
MAX_KEYFRAME_SAMPLE: Final[int] = 10
MAX_ACCEPTABLE_KEYFRAME_INTERVAL: Final[float] = 3.0

# ══════════════════════════════════════════════════════════════════════════════
#  UNIT CONVERSION
# ══════════════════════════════════════════════════════════════════════════════

BYTES_PER_MB: Final[int] = 1024 * 1024
MICROSECONDS_PER_SECOND: Final[int] = 1_000_000
SECONDS_PER_HOUR: Final[int] = 3600
SECONDS_PER_MINUTE: Final[int] = 60
BITS_PER_KBIT: Final[int] = 1000
