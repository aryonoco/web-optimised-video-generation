namespace WebOptimise

open System.Text.Json
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module ProbeParse =

    let private tryGetString (elem: JsonElement) (prop: string) : string option =
        let mutable child = Unchecked.defaultof<JsonElement>

        if elem.TryGetProperty(prop, &child) && child.ValueKind = JsonValueKind.String then
            child.GetString() |> Option.ofObj
        else
            None

    let private getString (elem: JsonElement) (prop: string) (fallback: string) : string =
        tryGetString elem prop |> Option.defaultValue fallback

    let private tryGetInt (elem: JsonElement) (prop: string) : int voption =
        let mutable child = Unchecked.defaultof<JsonElement>

        if elem.TryGetProperty(prop, &child) then
            let mutable v = 0

            if child.TryGetInt32(&v) then ValueSome v else ValueNone
        else
            ValueNone

    let private getInt (elem: JsonElement) (prop: string) : int =
        tryGetInt elem prop |> ValueOption.defaultValue 0

    let private findStream (codecType: string) (root: JsonElement) : JsonElement voption =
        let mutable streamsElem = Unchecked.defaultof<JsonElement>

        if
            root.TryGetProperty("streams", &streamsElem)
            && streamsElem.ValueKind = JsonValueKind.Array
        then
            let len = streamsElem.GetArrayLength()

            let rec scan i =
                if i >= len then
                    ValueNone
                else
                    let s = streamsElem[i]

                    if getString s "codec_type" "" = codecType then
                        ValueSome s
                    else
                        scan (i + 1)

            scan 0
        else
            ValueNone

    let private parseVideoStream (elem: JsonElement) : VideoStream =
        { Codec = getString elem "codec_name" "unknown"
          Profile = getString elem "profile" "unknown"
          Width = getInt elem "width" |> max 0
          Height = getInt elem "height" |> max 0
          FrameRate = getString elem "r_frame_rate" "0/1" |> Parse.frameRate
          Bitrate = tryGetString elem "bit_rate" |> Parse.safeInt64 }

    let private parseAudioStream (elem: JsonElement) : AudioStream =
        { Codec = getString elem "codec_name" "unknown"
          Channels = getInt elem "channels" |> max 0
          SampleRate = tryGetString elem "sample_rate" |> Parse.safeInt
          Bitrate = tryGetString elem "bit_rate" |> Parse.safeInt64 }

    let private parseFormat (root: JsonElement) : struct (float * int64) =
        let mutable fmtElem = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty("format", &fmtElem) then
            let duration = tryGetString fmtElem "duration" |> Parse.safeFloat
            let size = tryGetString fmtElem "size" |> Parse.safeFloat |> int64
            struct (duration, size)
        else
            struct (0.0, 0L)

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
                | ValueNone ->
                    Error(AppError.ProbeError $"No video stream found in %s{MediaFilePath.name path}")

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
