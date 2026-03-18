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

    let parseVideoStream (path: MediaFilePath) (elem: JsonElement) : Result<VideoStream, AppError> =
        result {
            let! codec =
                match elem with
                | Json.Prop "codec_name" (Json.Str s) -> Ok(VideoCodec.ofString s)
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.VideoCodec, path)))

            let! width =
                match elem with
                | Json.Prop "width" (Json.JInt w) when w > 0 -> Ok w
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.VideoWidth, path)))

            let! height =
                match elem with
                | Json.Prop "height" (Json.JInt h) when h > 0 -> Ok h
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.VideoHeight, path)))

            let! frameRate =
                match elem with
                | Json.Prop "r_frame_rate" (Json.Str s) ->
                    match Parse.frameRate s with
                    | ValueSome f when f > 0.0 -> Ok f
                    | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.FrameRate, path)))
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.FrameRate, path)))

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

    let parseAudioStream (path: MediaFilePath) (elem: JsonElement) : Result<AudioStream, AppError> =
        result {
            let! codec =
                match elem with
                | Json.Prop "codec_name" (Json.Str s) -> Ok(AudioCodec.ofString s)
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.AudioCodec, path)))

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

    let parseFormat (path: MediaFilePath) (root: JsonElement) : Result<struct (float * int64), AppError> =
        result {
            let! fmt =
                match root with
                | Json.Prop "format" fmt -> Ok fmt
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.FormatSection, path)))

            let! duration =
                match fmt with
                | Json.Prop "duration" (Json.Str(Float d)) when d > 0.0 -> Ok d
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.Duration, path)))

            let! size =
                match fmt with
                | Json.Prop "size" (Json.Str(Float s)) when s > 0.0 -> Ok(int64 s)
                | _ -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.FileSize, path)))

            return struct (duration, size)
        }

    let fromJson (path: MediaFilePath) (root: JsonElement) : Result<MediaFileInfo, AppError> =
        result {
            let! videoElem =
                match findStream "video" root with
                | ValueSome elem -> Ok elem
                | ValueNone -> Error(AppError.Probe(ProbeFailure.MissingField(ProbeField.VideoStream, path)))

            let! video = parseVideoStream path videoElem

            let! audio =
                match findStream "audio" root with
                | ValueSome elem -> parseAudioStream path elem |> Result.map ValueSome
                | ValueNone -> Ok ValueNone

            let! struct (duration, size) = parseFormat path root

            return {
                Path = path
                DurationSecs = duration
                SizeBytes = size
                Video = video
                Audio = audio
            }
        }
