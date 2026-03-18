namespace WebOptimise

open System
open System.Collections.Frozen

[<RequireQualifiedAccess>]
module Constants =

    let supportedExtensions: FrozenSet<string> =
        [ ".mp4"; ".m4v"; ".mov"; ".mkv" ].ToFrozenSet(StringComparer.OrdinalIgnoreCase)

    let mkvExtensions: FrozenSet<string> =
        [ ".mkv" ].ToFrozenSet(StringComparer.OrdinalIgnoreCase)

    [<Literal>]
    let OutputDirName = "web-optimised"

    // x264 encoding settings
    [<Literal>]
    let Crf = 25

    [<Literal>]
    let Preset = "slower"

    [<Literal>]
    let Profile = "high"

    [<Literal>]
    let Level = "4.0"

    [<Literal>]
    let KeyframeIntervalSecs = 2

    [<Literal>]
    let BFrames = 3

    [<Literal>]
    let X264Params = "deblock=-1,-1"

    // Resolution thresholds
    [<Literal>]
    let Width4K = 3840

    [<Literal>]
    let Width1440p = 2560

    [<Literal>]
    let Width1080p = 1920

    [<Literal>]
    let Width720p = 1280

    // Verification thresholds
    [<Literal>]
    let MinKeyframesForCheck = 2

    [<Literal>]
    let MaxKeyframeSample = 10

    [<Literal>]
    let MaxAcceptableKeyframeInterval = 3.0

    // Unit conversion
    [<Literal>]
    let BytesPerMB = 1_048_576L

    [<Literal>]
    let MicrosecondsPerSecond = 1_000_000L

    [<Literal>]
    let SecondsPerHour = 3600

    [<Literal>]
    let SecondsPerMinute = 60

    [<Literal>]
    let BitsPerKbit = 1000L

    // EBML binary constants
    [<Literal>]
    let HeaderScanSize = 1_048_576 // 1 MB

[<Struct; RequireQualifiedAccess>]
type Mode =
    | Remux
    | Encode
    | Webm
