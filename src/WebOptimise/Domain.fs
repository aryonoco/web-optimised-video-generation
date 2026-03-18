namespace WebOptimise

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module NullSafe =

    let inline path (s: string | null) =
        match s with
        | null -> ""
        | v -> v

// Branded types

[<Struct>]
type MediaFilePath = private | MediaFilePath of string

[<RequireQualifiedAccess>]
module MediaFilePath =

    let create (path: string) : Result<MediaFilePath, string> =
        if String.IsNullOrWhiteSpace path then
            Error "File path must not be empty"
        else
            Ok(MediaFilePath path)

    let value (MediaFilePath p) = p

    let ofTrusted (path: string) = MediaFilePath path

    let name (MediaFilePath p) = Path.GetFileName p |> NullSafe.path

    let extension (MediaFilePath p) = Path.GetExtension p |> NullSafe.path

    let directory (MediaFilePath p) =
        Path.GetDirectoryName p |> NullSafe.path

[<Struct>]
type OutputExtension = private | OutputExtension of string

[<RequireQualifiedAccess>]
module OutputExtension =

    let mp4 = OutputExtension ".mp4"

    let webm = OutputExtension ".webm"

    let value (OutputExtension e) = e

[<Struct; RequireQualifiedAccess>]
type ShellError =
    | NotFound of tool: string
    | Cancelled
    | NonZeroExit of tool: string * exitCode: int * stderr: string
    | Failed of tool: string * message: string

[<RequireQualifiedAccess>]
module ShellError =

    let format =
        function
        | ShellError.NotFound tool -> $"%s{tool} not found in PATH"
        | ShellError.Cancelled -> "Operation cancelled"
        | ShellError.NonZeroExit(tool, code, stderr) -> $"%s{tool} exited with code %d{code}\n%s{stderr}"
        | ShellError.Failed(tool, msg) -> $"%s{tool}: %s{msg}"

[<Struct>]
type OutputPath = private | OutputPath of string

[<RequireQualifiedAccess>]
module OutputPath =

    let create (dir: string) (filename: string) = OutputPath(Path.Combine(dir, filename))

    let ofFullPath (path: string) = OutputPath path

    let value (OutputPath p) = p

    let fileName (OutputPath p) = Path.GetFileName p |> NullSafe.path

[<RequireQualifiedAccess>]
type ResolvedPath =
    | File of fullPath: string * ext: string
    | Directory of fullPath: string * files: string list
    | NotFound of originalPath: string

// Error DU

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type AppError =
    | ProbeError of message: string
    | EncodeError of message: string * cmd: string list voption
    | ValidationError of message: string
    | IoError of message: string

[<RequireQualifiedAccess>]
module AppError =

    let message =
        function
        | AppError.ProbeError msg -> msg
        | AppError.EncodeError(msg, _) -> msg
        | AppError.ValidationError msg -> msg
        | AppError.IoError msg -> msg

    let ofShellError (e: ShellError) =
        match e with
        | ShellError.NotFound _ -> AppError.ValidationError(ShellError.format e)
        | ShellError.Cancelled -> AppError.IoError(ShellError.format e)
        | ShellError.NonZeroExit _ -> AppError.EncodeError(ShellError.format e, ValueNone)
        | ShellError.Failed _ -> AppError.EncodeError(ShellError.format e, ValueNone)

[<Struct; RequireQualifiedAccess>]
type Mode =
    | Remux
    | Encode
    | Webm

[<Struct; RequireQualifiedAccess>]
type VideoCodec =
    | H264
    | H265
    | AV1
    | VP9
    | VP8
    | Other of videoCodecName: string

[<RequireQualifiedAccess>]
module VideoCodec =

    let ofString =
        function
        | "h264" -> VideoCodec.H264
        | "hevc"
        | "h265" -> VideoCodec.H265
        | "av1" -> VideoCodec.AV1
        | "vp9" -> VideoCodec.VP9
        | "vp8" -> VideoCodec.VP8
        | other -> VideoCodec.Other other

    let displayName =
        function
        | VideoCodec.H264 -> "H.264"
        | VideoCodec.H265 -> "H.265"
        | VideoCodec.AV1 -> "AV1"
        | VideoCodec.VP9 -> "VP9"
        | VideoCodec.VP8 -> "VP8"
        | VideoCodec.Other n -> n

[<Struct; RequireQualifiedAccess>]
type AudioCodec =
    | AAC
    | Opus
    | Other of audioCodecName: string

[<RequireQualifiedAccess>]
module AudioCodec =

    let ofString =
        function
        | "aac" -> AudioCodec.AAC
        | "opus" -> AudioCodec.Opus
        | other -> AudioCodec.Other other

    let displayName =
        function
        | AudioCodec.AAC -> "AAC"
        | AudioCodec.Opus -> "Opus"
        | AudioCodec.Other n -> n

[<Struct; RequireQualifiedAccess>]
type VideoProfile =
    | High
    | Main
    | Baseline
    | Other of videoProfileName: string

[<RequireQualifiedAccess>]
module VideoProfile =

    let ofString (s: string) =
        if s.Contains("High", StringComparison.Ordinal) then
            VideoProfile.High
        elif s.Contains("Main", StringComparison.Ordinal) then
            VideoProfile.Main
        elif s.Contains("Baseline", StringComparison.Ordinal) then
            VideoProfile.Baseline
        else
            VideoProfile.Other s

    let displayName =
        function
        | VideoProfile.High -> "High"
        | VideoProfile.Main -> "Main"
        | VideoProfile.Baseline -> "Baseline"
        | VideoProfile.Other n -> n

// Domain records

[<NoComparison; NoEquality>]
type VideoStream = {
    Codec: VideoCodec
    Profile: VideoProfile voption
    Width: int
    Height: int
    FrameRate: float
    Bitrate: int64 voption
}

[<NoComparison; NoEquality>]
type AudioStream = {
    Codec: AudioCodec
    Channels: int voption
    SampleRate: int voption
    Bitrate: int64 voption
}

[<NoComparison; NoEquality>]
type MediaFileInfo = {
    Path: MediaFilePath
    DurationSecs: float
    SizeBytes: int64
    Video: VideoStream
    Audio: AudioStream voption
}

[<NoComparison; NoEquality>]
type EncodeResult = {
    InputPath: MediaFilePath
    OutputPath: OutputPath
    InputSize: int64
    OutputSize: int64
}

[<NoComparison; NoEquality>]
type ProcessResult = {
    Result: EncodeResult
    Mode: Mode
}

// Module functions for computed properties

[<RequireQualifiedAccess>]
module VideoStream =

    let resolutionLabel (s: VideoStream) =
        if s.Width >= Constants.Width4K then
            "4K"
        elif s.Width >= Constants.Width1440p then
            "1440p"
        elif s.Width >= Constants.Width1080p then
            "1080p"
        elif s.Width >= Constants.Width720p then
            "720p"
        else
            $"%d{s.Width}x%d{s.Height}"

    let bitrateKbps (s: VideoStream) =
        match s.Bitrate with
        | ValueSome br -> $"%d{br / Constants.BitsPerKbit} kbps"
        | ValueNone -> "N/A"

[<RequireQualifiedAccess>]
module MediaFileInfo =

    let durationDisplay (info: MediaFileInfo) =
        let total = int info.DurationSecs
        let hours = total / Constants.SecondsPerHour
        let remainder = total % Constants.SecondsPerHour
        let minutes = remainder / Constants.SecondsPerMinute
        let seconds = remainder % Constants.SecondsPerMinute

        if hours > 0 then
            $"%d{hours}h %02d{minutes}m %02d{seconds}s"
        else
            $"%d{minutes}m %02d{seconds}s"

    let sizeMB (info: MediaFileInfo) =
        float info.SizeBytes / float Constants.BytesPerMB

[<RequireQualifiedAccess>]
module EncodeResult =

    let savingsPct (r: EncodeResult) =
        if r.InputSize = 0L then
            0.0
        else
            float (r.OutputSize - r.InputSize) / float r.InputSize
            * 100.0

    let savingsDisplay (r: EncodeResult) =
        let pct = savingsPct r
        let sign = if pct > 0.0 then "+" else ""
        $"%s{sign}%.1f{pct}%%"

// Active patterns

[<AutoOpen>]
module internal ActivePatterns =

    [<return: Struct>]
    let (|Int|_|) (s: string) =
        match System.Int32.TryParse s with
        | true, v -> ValueSome v
        | _ -> ValueNone

    [<return: Struct>]
    let (|Int64|_|) (s: string) =
        match System.Int64.TryParse s with
        | true, v -> ValueSome v
        | _ -> ValueNone

    [<return: Struct>]
    let (|Float|_|) (s: string) =
        match System.Double.TryParse s with
        | true, v -> ValueSome v
        | _ -> ValueNone

[<RequireQualifiedAccess>]
module Json =

    [<return: Struct>]
    let (|Prop|_|) (name: string) (elem: JsonElement) =
        let mutable child = Unchecked.defaultof<JsonElement>

        if elem.TryGetProperty(name, &child) then
            ValueSome child
        else
            ValueNone

    [<return: Struct>]
    let (|Str|_|) (elem: JsonElement) =
        if elem.ValueKind = JsonValueKind.String then
            elem.GetString() |> ValueOption.ofObj
        else
            ValueNone

    [<return: Struct>]
    let (|JInt|_|) (elem: JsonElement) =
        let mutable v = 0

        if elem.TryGetInt32(&v) then
            ValueSome v
        else
            ValueNone

    [<return: Struct>]
    let (|Arr|_|) (elem: JsonElement) =
        if elem.ValueKind = JsonValueKind.Array then
            ValueSome(seq { for i in 0 .. elem.GetArrayLength() - 1 -> elem[i] })
        else
            ValueNone

// Pure helpers

[<RequireQualifiedAccess>]
module Parse =

    let frameRate (fpsStr: string) : float voption =
        match fpsStr.Split('/') with
        | [| Int n; Int d |] when d <> 0 -> ValueSome(float n / float d)
        | [| Float f |] -> ValueSome f
        | _ -> ValueNone

    let safeInt64 =
        function
        | Some(Int64 v) -> ValueSome v
        | _ -> ValueNone
