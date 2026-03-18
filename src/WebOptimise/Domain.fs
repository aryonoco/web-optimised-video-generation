namespace WebOptimise

open System
open System.IO
open System.Text.Json
open FsToolkit.ErrorHandling

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
type NonEmpty<'T> = | NonEmpty of head: 'T * tail: 'T list

[<RequireQualifiedAccess>]
module NonEmpty =

    let singleton (v: 'T) : NonEmpty<'T> = NonEmpty(v, [])

    let ofList (xs: 'T list) : NonEmpty<'T> voption =
        match xs with
        | [] -> ValueNone
        | h :: t -> ValueSome(NonEmpty(h, t))

    let toList (NonEmpty(h, t)) : 'T list = h :: t

    let head (NonEmpty(h, _)) : 'T = h

    let length (NonEmpty(_, t)) : int = 1 + List.length t

    let map (f: 'T -> 'U) (NonEmpty(h, t)) : NonEmpty<'U> = NonEmpty(f h, List.map f t)

    let fold (folder: 'S -> 'T -> 'S) (state: 'S) (NonEmpty(h, t)) : 'S = List.fold folder (folder state h) t

    let iter (f: 'T -> unit) (NonEmpty(h, t)) : unit =
        f h
        List.iter f t

    let traverseResultM (f: 'T -> Result<'U, 'E>) (NonEmpty(h, t)) : Result<NonEmpty<'U>, 'E> =
        result {
            let! h' = f h
            let! t' = List.traverseResultM f t
            return NonEmpty(h', t')
        }

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

// Structured error types

[<Struct; RequireQualifiedAccess>]
type ProbeField =
    | VideoCodec
    | VideoWidth
    | VideoHeight
    | FrameRate
    | AudioCodec
    | FormatSection
    | Duration
    | FileSize
    | VideoStream

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type ProbeFailure =
    | ShellFailed of shellError: ShellError * file: MediaFilePath
    | NonZeroExit of exitCode: int * stderr: string * file: MediaFilePath
    | JsonParseFailed of exnMessage: string * file: MediaFilePath
    | MissingField of field: ProbeField * file: MediaFilePath

[<Struct; RequireQualifiedAccess; NoComparison; NoEquality>]
type MkvCodecViolation =
    | WrongVideoCodec of mkvFileName: string * mkvActualCodec: VideoCodec
    | MissingAudio of mkvMissingFileName: string
    | WrongAudioCodec of mkvAudioFileName: string * mkvActualAudioCodec: AudioCodec

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type ValidationFailure =
    | UnknownMode of input: string
    | NoPaths
    | NoSupportedFiles
    | UnsupportedExtension of ext: string * fullPath: string
    | PathNotFound of path: string
    | InvalidPath of reason: string
    | MkvEncodeNotSupported of fileNames: string list
    | MkvCodecViolations of violations: MkvCodecViolation list
    | ArguError of argu: string
    | ToolNotFound of toolName: string

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type ProcessFailure =
    | FfmpegFailed of exitCode: int * stderr: string * cmd: string list * fileName: string * verb: string
    | Cancelled
    | OutputDirFailed of dirPath: string * exnMessage: string
    | ShellFailed of shellError: ShellError

[<Struct; RequireQualifiedAccess>]
type VerificationIssue =
    | ProfileMismatch of expected: string * actual: string
    | NoVideoStream
    | FaststartMissing
    | AtomPositionUnknown
    | KeyframeIntervalTooLarge of avgInterval: float * expectedSecs: int
    | CuesNotFront of cuesReason: string
    | CheckFailed of checkReason: string
    | FileReadFailed of readReason: string

[<RequireQualifiedAccess>]
module VerificationIssue =

    let format =
        function
        | VerificationIssue.ProfileMismatch(expected, actual) -> $"Expected %s{expected} profile, got '%s{actual}'"
        | VerificationIssue.NoVideoStream -> "No video stream found"
        | VerificationIssue.FaststartMissing -> "faststart not applied: moov atom is after mdat"
        | VerificationIssue.AtomPositionUnknown -> "Could not determine moov/mdat positions"
        | VerificationIssue.KeyframeIntervalTooLarge(avg, expected) ->
            $"Keyframe interval too large: %.1f{avg}s (expected ~%d{expected}s)"
        | VerificationIssue.CuesNotFront reason -> reason
        | VerificationIssue.CheckFailed reason -> reason
        | VerificationIssue.FileReadFailed reason -> $"Could not read file for verification: %s{reason}"

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type AppError =
    | Probe of ProbeFailure
    | Validation of ValidationFailure
    | Process of ProcessFailure
    | Multiple of errors: NonEmpty<AppError>

[<RequireQualifiedAccess>]
module AppError =

    let private formatProbeField =
        function
        | ProbeField.VideoCodec -> "video codec"
        | ProbeField.VideoWidth -> "video width"
        | ProbeField.VideoHeight -> "video height"
        | ProbeField.FrameRate -> "frame rate"
        | ProbeField.AudioCodec -> "audio codec"
        | ProbeField.FormatSection -> "format section"
        | ProbeField.Duration -> "duration"
        | ProbeField.FileSize -> "file size"
        | ProbeField.VideoStream -> "video stream"

    let private formatProbe =
        function
        | ProbeFailure.ShellFailed(e, file) ->
            $"ffprobe failed for %s{MediaFilePath.name file}: %s{ShellError.format e}"
        | ProbeFailure.NonZeroExit(code, stderr, file) ->
            $"ffprobe failed for %s{MediaFilePath.name file}: exit code %d{code}\n%s{stderr}"
        | ProbeFailure.JsonParseFailed(msg, file) ->
            $"Failed to parse ffprobe JSON for %s{MediaFilePath.name file}: %s{msg}"
        | ProbeFailure.MissingField(field, file) ->
            $"Missing or invalid %s{formatProbeField field} in %s{MediaFilePath.name file}"

    let private formatMkvViolation =
        function
        | MkvCodecViolation.WrongVideoCodec(name, codec) ->
            $"  %s{name}: video codec is %s{VideoCodec.displayName codec} (requires AV1 for WebM)"
        | MkvCodecViolation.MissingAudio name -> $"  %s{name}: no audio stream found (requires Opus for WebM)"
        | MkvCodecViolation.WrongAudioCodec(name, codec) ->
            $"  %s{name}: audio codec is %s{AudioCodec.displayName codec} (requires Opus for WebM)"

    let private formatValidation =
        function
        | ValidationFailure.UnknownMode s -> $"Unknown mode: '%s{s}'. Use 'remux' or 'encode'."
        | ValidationFailure.NoPaths -> "No paths provided."
        | ValidationFailure.NoSupportedFiles -> "No supported media files found."
        | ValidationFailure.UnsupportedExtension(ext, path) -> $"Unsupported file type '%s{ext}': %s{path}"
        | ValidationFailure.PathNotFound path -> $"Path does not exist: %s{path}"
        | ValidationFailure.InvalidPath reason -> $"Invalid path: %s{reason}"
        | ValidationFailure.MkvEncodeNotSupported names ->
            let joined = names |> String.concat ", "
            $"--mode encode is not supported for MKV files: %s{joined}"
        | ValidationFailure.MkvCodecViolations violations ->
            "MKV codec requirements not met:\n"
            + (violations
               |> List.map formatMkvViolation
               |> String.concat "\n")
        | ValidationFailure.ArguError msg -> msg
        | ValidationFailure.ToolNotFound tool -> $"%s{tool} not found in PATH"

    let private formatProcess =
        function
        | ProcessFailure.FfmpegFailed(code, stderr, _, fileName, verb) ->
            $"%s{verb} failed for %s{fileName}: exit code %d{code}\n%s{stderr}"
        | ProcessFailure.Cancelled -> "Operation cancelled"
        | ProcessFailure.OutputDirFailed(dir, msg) -> $"Cannot create output directory '%s{dir}': %s{msg}"
        | ProcessFailure.ShellFailed e -> ShellError.format e

    let rec format =
        function
        | AppError.Probe e -> formatProbe e
        | AppError.Validation e -> formatValidation e
        | AppError.Process e -> formatProcess e
        | AppError.Multiple errors ->
            errors
            |> NonEmpty.toList
            |> List.map format
            |> String.concat "\n"

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

[<NoComparison; NoEquality>]
type X264Settings = {
    Preset: string
    Crf: int
    Profile: string
    Level: string
    GopSize: int
    MinKeyint: int
    BFrames: int
    X264Params: string
}

type ProgressReporter = float -> unit

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
