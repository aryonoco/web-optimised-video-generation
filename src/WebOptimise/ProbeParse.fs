namespace WebOptimise

open System.Text.Json
open System.Text.Json.Serialization
open FsToolkit.ErrorHandling

#nowarn 3261

module ProbeParse =

    let private jsonOptions =
        let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts.Converters.Add(JsonFSharpConverter())
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let private findFirstStream (codecType: string) (streams: RawStream array | null) =
        match streams with
        | null -> None
        | arr -> arr |> Array.tryFind (fun s -> s.CodecType = codecType)

    let private parseVideoStream (raw: RawStream) : VideoStream =
        let fps =
            if isNull raw.RFrameRate then 0.0
            else Parse.frameRate raw.RFrameRate

        { Codec = if isNull raw.CodecName then "unknown" else raw.CodecName
          Profile = if isNull raw.Profile then "unknown" else raw.Profile
          Width = max raw.Width 0
          Height = max raw.Height 0
          FrameRate = fps
          Bitrate = Parse.safeInt64 raw.BitRate }

    let private parseAudioStream (raw: RawStream) : AudioStream =
        { Codec = if isNull raw.CodecName then "unknown" else raw.CodecName
          Channels = max raw.Channels 0
          SampleRate = Parse.safeInt raw.SampleRate
          Bitrate = Parse.safeInt64 raw.BitRate }

    let fromJson (path: MediaFilePath) (json: string) : Result<MediaFileInfo, AppError> =
        result {
            let! data =
                try
                    Ok(JsonSerializer.Deserialize<RawProbeData>(json, jsonOptions))
                with ex ->
                    Error(AppError.ProbeError $"Failed to parse ffprobe JSON for %s{MediaFilePath.name path}: %s{ex.Message}")

            let! videoRaw =
                findFirstStream "video" data.Streams
                |> Result.requireSome (AppError.ProbeError $"No video stream found in %s{MediaFilePath.name path}")

            let video = parseVideoStream videoRaw

            let audio =
                findFirstStream "audio" data.Streams
                |> Option.map parseAudioStream
                |> Option.toValueOption

            let fmt =
                if isNull (box data.Format) then
                    { Duration = "0"; Size = "0" }
                else
                    data.Format

            return
                { Path = path
                  DurationSecs = Parse.safeFloat fmt.Duration
                  SizeBytes = Parse.safeFloat fmt.Size |> int64
                  Video = video
                  Audio = audio }
        }
