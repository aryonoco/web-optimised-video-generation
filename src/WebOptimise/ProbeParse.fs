namespace WebOptimise

open System.Text.Json
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module ProbeParse =

    let private findStream (codecType: string) (root: JsonElement) : JsonElement voption =
        match root with
        | Json.Prop "streams" (Json.Arr streams) ->
            streams
            |> Seq.tryFind (fun s ->
                match s with
                | Json.Prop "codec_type" (Json.Str ct) -> ct = codecType
                | _ -> false)
            |> ValueOption.ofOption
        | _ -> ValueNone

    let private parseVideoStream (elem: JsonElement) : VideoStream =
        { Codec =
            match elem with
            | Json.Prop "codec_name" (Json.Str s) -> s
            | _ -> "unknown"
          Profile =
            match elem with
            | Json.Prop "profile" (Json.Str s) -> s
            | _ -> "unknown"
          Width =
            match elem with
            | Json.Prop "width" (Json.JInt w) -> max 0 w
            | _ -> 0
          Height =
            match elem with
            | Json.Prop "height" (Json.JInt h) -> max 0 h
            | _ -> 0
          FrameRate =
            match elem with
            | Json.Prop "r_frame_rate" (Json.Str s) -> Parse.frameRate s
            | _ -> 0.0
          Bitrate =
            match elem with
            | Json.Prop "bit_rate" (Json.Str s) -> Parse.safeInt64 (Some s)
            | _ -> ValueNone }

    let private parseAudioStream (elem: JsonElement) : AudioStream =
        { Codec =
            match elem with
            | Json.Prop "codec_name" (Json.Str s) -> s
            | _ -> "unknown"
          Channels =
            match elem with
            | Json.Prop "channels" (Json.JInt c) -> max 0 c
            | _ -> 0
          SampleRate =
            match elem with
            | Json.Prop "sample_rate" (Json.Str s) -> Parse.safeInt (Some s)
            | _ -> 0
          Bitrate =
            match elem with
            | Json.Prop "bit_rate" (Json.Str s) -> Parse.safeInt64 (Some s)
            | _ -> ValueNone }

    let private parseFormat (root: JsonElement) : struct (float * int64) =
        match root with
        | Json.Prop "format" fmt ->
            let duration =
                match fmt with
                | Json.Prop "duration" (Json.Str s) -> Parse.safeFloat (Some s)
                | _ -> 0.0

            let size =
                match fmt with
                | Json.Prop "size" (Json.Str s) -> Parse.safeFloat (Some s) |> int64
                | _ -> 0L

            struct (duration, size)
        | _ -> struct (0.0, 0L)

    let fromJson (path: MediaFilePath) (json: string) : Result<MediaFileInfo, AppError> =
        result {
            let! root =
                try
                    Ok(JsonDocument.Parse(json).RootElement)
                with ex ->
                    Error(
                        AppError.ProbeError
                            $"Failed to parse ffprobe JSON for %s{MediaFilePath.name path}: %s{ex.Message}"
                    )

            let! videoElem =
                match findStream "video" root with
                | ValueSome elem -> Ok elem
                | ValueNone -> Error(AppError.ProbeError $"No video stream found in %s{MediaFilePath.name path}")

            let video = parseVideoStream videoElem
            let audio = findStream "audio" root |> ValueOption.map parseAudioStream
            let struct (duration, size) = parseFormat root

            return
                { Path = path
                  DurationSecs = duration
                  SizeBytes = size
                  Video = video
                  Audio = audio }
        }
