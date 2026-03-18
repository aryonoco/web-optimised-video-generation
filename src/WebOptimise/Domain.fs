namespace WebOptimise

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
type MediaFilePath = private MediaFilePath of string

[<RequireQualifiedAccess>]
module MediaFilePath =

    let create (path: string) : Result<MediaFilePath, string> =
        if File.Exists path then
            Ok(MediaFilePath path)
        else
            Error $"File does not exist: %s{path}"

    let value (MediaFilePath p) = p

    let ofTrusted (path: string) = MediaFilePath path

    let name (MediaFilePath p) = Path.GetFileName p |> NullSafe.path

    let extension (MediaFilePath p) = Path.GetExtension p |> NullSafe.path

    let directory (MediaFilePath p) =
        Path.GetDirectoryName p |> NullSafe.path

[<Struct>]
type OutputExtension = private OutputExtension of string

[<RequireQualifiedAccess>]
module OutputExtension =

    let mp4 = OutputExtension ".mp4"

    let webm = OutputExtension ".webm"

    let value (OutputExtension e) = e

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

[<Struct; RequireQualifiedAccess>]
type Mode =
    | Remux
    | Encode
    | Webm

// Domain records

[<NoComparison; NoEquality>]
type VideoStream =
    { Codec: string
      Profile: string
      Width: int
      Height: int
      FrameRate: float
      Bitrate: int64 voption }

[<NoComparison; NoEquality>]
type AudioStream =
    { Codec: string
      Channels: int
      SampleRate: int
      Bitrate: int64 voption }

[<NoComparison; NoEquality>]
type MediaFileInfo =
    { Path: MediaFilePath
      DurationSecs: float
      SizeBytes: int64
      Video: VideoStream
      Audio: AudioStream voption }

[<NoComparison; NoEquality>]
type EncodeResult =
    { InputPath: MediaFilePath
      OutputPath: string
      InputSize: int64
      OutputSize: int64 }

type ProcessResult = EncodeResult * Mode

// Module functions for computed properties

[<RequireQualifiedAccess>]
module VideoStream =

    let resolutionLabel (s: VideoStream) =
        if s.Width >= Constants.Width4K then "4K"
        elif s.Width >= Constants.Width1440p then "1440p"
        elif s.Width >= Constants.Width1080p then "1080p"
        elif s.Width >= Constants.Width720p then "720p"
        else $"%d{s.Width}x%d{s.Height}"

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
            float (r.OutputSize - r.InputSize) / float r.InputSize * 100.0

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
        if elem.TryGetInt32(&v) then ValueSome v else ValueNone

    [<return: Struct>]
    let (|Arr|_|) (elem: JsonElement) =
        if elem.ValueKind = JsonValueKind.Array then
            ValueSome(seq { for i in 0 .. elem.GetArrayLength() - 1 -> elem[i] })
        else
            ValueNone

// Pure helpers

[<RequireQualifiedAccess>]
module Parse =

    let frameRate (fpsStr: string) : float =
        match fpsStr.Split('/') with
        | [| Int n; Int d |] when d <> 0 -> float n / float d
        | [| Float f |] -> f
        | _ -> 0.0

    let safeInt64 =
        function
        | Some(Int64 v) -> ValueSome v
        | _ -> ValueNone

    let safeInt =
        function
        | Some(Int v) -> v
        | _ -> 0

    let safeFloat =
        function
        | Some(Float v) -> v
        | _ -> 0.0
