namespace WebOptimise

open System.Text.Json
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module ProbeParse =

    let findStream (codecType: string) (root: JsonElement) : JsonElement voption =
        match root with
        | Json.Prop "streams" (Json.Arr streams) ->
            streams
            |> Seq.tryFind (fun s ->
                match s with
                | Json.Prop "codec_type" (Json.Str ct) -> ct = codecType
                | _ -> false
            )
            |> ValueOption.ofOption
        | _ -> ValueNone

    let parseVideoStream (fileName: string) (elem: JsonElement) : Result<VideoStream, AppError> =
        result {
            let! codec =
                match elem with
                | Json.Prop "codec_name" (Json.Str s) -> Ok(VideoCodec.ofString s)
                | _ -> Error(AppError.ProbeError $"Missing video codec in %s{fileName}")

            let! width =
                match elem with
                | Json.Prop "width" (Json.JInt w) when w > 0 -> Ok w
                | _ -> Error(AppError.ProbeError $"Missing or invalid video width in %s{fileName}")

            let! height =
                match elem with
                | Json.Prop "height" (Json.JInt h) when h > 0 -> Ok h
                | _ -> Error(AppError.ProbeError $"Missing or invalid video height in %s{fileName}")

            let! frameRate =
                match elem with
                | Json.Prop "r_frame_rate" (Json.Str s) ->
                    match Parse.frameRate s with
                    | ValueSome f when f > 0.0 -> Ok f
                    | _ -> Error(AppError.ProbeError $"Invalid frame rate in %s{fileName}")
                | _ -> Error(AppError.ProbeError $"Missing frame rate in %s{fileName}")

            let profile =
                match elem with
                | Json.Prop "profile" (Json.Str s) -> ValueSome(VideoProfile.ofString s)
                | _ -> ValueNone

            let bitrate =
                match elem with
                | Json.Prop "bit_rate" (Json.Str s) -> Parse.safeInt64 (Some s)
                | _ -> ValueNone

            return {
                Codec = codec
                Profile = profile
                Width = width
                Height = height
                FrameRate = frameRate
                Bitrate = bitrate
            }
        }

    let parseAudioStream (fileName: string) (elem: JsonElement) : Result<AudioStream, AppError> =
        result {
            let! codec =
                match elem with
                | Json.Prop "codec_name" (Json.Str s) -> Ok(AudioCodec.ofString s)
                | _ -> Error(AppError.ProbeError $"Missing audio codec in %s{fileName}")

            return {
                Codec = codec
                Channels =
                    match elem with
                    | Json.Prop "channels" (Json.JInt c) when c > 0 -> ValueSome c
                    | _ -> ValueNone
                SampleRate =
                    match elem with
                    | Json.Prop "sample_rate" (Json.Str s) ->
                        match s with
                        | Int v when v > 0 -> ValueSome v
                        | _ -> ValueNone
                    | _ -> ValueNone
                Bitrate =
                    match elem with
                    | Json.Prop "bit_rate" (Json.Str s) -> Parse.safeInt64 (Some s)
                    | _ -> ValueNone
            }
        }

    let parseFormat (fileName: string) (root: JsonElement) : Result<struct (float * int64), AppError> =
        result {
            let! fmt =
                match root with
                | Json.Prop "format" fmt -> Ok fmt
                | _ -> Error(AppError.ProbeError $"Missing format section in %s{fileName}")

            let! duration =
                match fmt with
                | Json.Prop "duration" (Json.Str(Float d)) when d > 0.0 -> Ok d
                | _ -> Error(AppError.ProbeError $"Missing or invalid duration in %s{fileName}")

            let! size =
                match fmt with
                | Json.Prop "size" (Json.Str(Float s)) when s > 0.0 -> Ok(int64 s)
                | _ -> Error(AppError.ProbeError $"Missing or invalid file size in %s{fileName}")

            return struct (duration, size)
        }

    let fromJson (path: MediaFilePath) (json: string) : Result<MediaFileInfo, AppError> =
        result {
            let fileName = MediaFilePath.name path

            let! root =
                try
                    Ok(JsonElement.Parse(json))
                with ex ->
                    Error(AppError.ProbeError $"Failed to parse ffprobe JSON for %s{fileName}: %s{ex.Message}")

            let! videoElem =
                match findStream "video" root with
                | ValueSome elem -> Ok elem
                | ValueNone -> Error(AppError.ProbeError $"No video stream found in %s{fileName}")

            let! video = parseVideoStream fileName videoElem

            let! audio =
                match findStream "audio" root with
                | ValueSome elem -> parseAudioStream fileName elem |> Result.map ValueSome
                | ValueNone -> Ok ValueNone

            let! struct (duration, size) = parseFormat fileName root

            return {
                Path = path
                DurationSecs = duration
                SizeBytes = size
                Video = video
                Audio = audio
            }
        }
