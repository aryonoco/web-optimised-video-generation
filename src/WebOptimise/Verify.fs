namespace WebOptimise

open System
open System.IO
open System.Text.Json
open CliWrap
open CliWrap.Buffered

/// Output file verification. Mix of shell (CliWrap subprocess) and pure checks.
[<RequireQualifiedAccess>]
module Verify =

    let private runFfprobe (args: string list) =
        try
            let result =
                Cli
                    .Wrap("ffprobe")
                    .WithArguments(args)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync()
                    .GetAwaiter()
                    .GetResult()

            Ok result
        with ex ->
            Error $"ffprobe failed: %s{ex.Message}"

    let private checkVideoProfile (path: string) : string option =
        match runFfprobe [ "-v"; "quiet"; "-print_format"; "json"; "-show_streams"; path ] with
        | Error msg -> Some msg
        | Ok result ->
            try
                let doc = JsonDocument.Parse(result.StandardOutput)
                let streams = doc.RootElement.GetProperty("streams")

                let videoStream =
                    seq { for i in 0 .. streams.GetArrayLength() - 1 -> streams[i] }
                    |> Seq.tryFind (fun s ->
                        let mutable elem = Unchecked.defaultof<JsonElement>
                        s.TryGetProperty("codec_type", &elem) && elem.GetString() = "video")

                match videoStream with
                | None -> Some "No video stream found"
                | Some stream ->
                    let mutable profileElem = Unchecked.defaultof<JsonElement>

                    let profile =
                        if stream.TryGetProperty("profile", &profileElem) then
                            profileElem.GetString() |> Option.ofObj |> Option.defaultValue ""
                        else
                            ""

                    if not (profile.Contains("High", StringComparison.Ordinal)) then
                        Some $"Expected High profile, got '%s{profile}'"
                    else
                        None
            with ex ->
                Some $"Failed to parse ffprobe output: %s{ex.Message}"

    let private checkFaststart (path: string) : string option =
        match runFfprobe [ "-v"; "trace"; path ] with
        | Error msg -> Some msg
        | Ok result ->
            let stderr = result.StandardError
            let moovPos = stderr.IndexOf("type:'moov'", StringComparison.Ordinal)
            let mdatPos = stderr.IndexOf("type:'mdat'", StringComparison.Ordinal)

            if moovPos = -1 || mdatPos = -1 then
                Some "Could not determine moov/mdat positions"
            elif moovPos > mdatPos then
                Some "faststart not applied: moov atom is after mdat"
            else
                None

    let private parseKeyframeTimes (output: string) : float list =
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            if line.Contains(",K", StringComparison.Ordinal) then
                let pts = line.AsSpan().Slice(0, max 0 (line.IndexOf(','))).ToString()

                match Double.TryParse pts with
                | true, v -> Some v
                | _ -> None
            else
                None)
        |> Array.toList

    let private checkKeyframeIntervals (path: string) : string option =
        match
            runFfprobe
                [ "-v"
                  "quiet"
                  "-select_streams"
                  "v:0"
                  "-show_entries"
                  "packet=pts_time,flags"
                  "-of"
                  "csv=p=0"
                  path ]
        with
        | Error msg -> Some msg
        | Ok result ->
            let keyframeTimes = parseKeyframeTimes result.StandardOutput

            if keyframeTimes.Length >= Constants.MinKeyframesForCheck then
                let sampleCount = min (keyframeTimes.Length - 1) Constants.MaxKeyframeSample

                let intervals =
                    [ for i in 0 .. sampleCount - 1 -> keyframeTimes[i + 1] - keyframeTimes[i] ]

                let avgInterval = List.average intervals

                if avgInterval > Constants.MaxAcceptableKeyframeInterval then
                    Some
                        $"Keyframe interval too large: %.1f{avgInterval}s (expected ~%d{Constants.KeyframeIntervalSecs}s)"
                else
                    None
            else
                None

    let private checkCuesFront (path: string) : string option =
        try
            let data =
                use fs = File.OpenRead(path)
                let buf = Array.zeroCreate (min (int fs.Length) Constants.HeaderScanSize)
                let bytesRead = fs.Read(buf, 0, buf.Length)
                ReadOnlySpan(buf, 0, bytesRead)

            match Ebml.checkCuesBeforeCluster data with
            | Ok() -> None
            | Error msg -> Some msg
        with ex ->
            Some $"Could not read file for verification: %s{ex.Message}"

    let verifyEncoded (path: string) : Result<unit, string list> =
        let issues =
            [ checkVideoProfile path; checkFaststart path; checkKeyframeIntervals path ]
            |> List.choose id

        if issues.IsEmpty then Ok() else Error issues

    let verifyRemuxed (path: string) : Result<unit, string list> =
        let issues = [ checkFaststart path ] |> List.choose id
        if issues.IsEmpty then Ok() else Error issues

    let verifyWebm (path: string) : Result<unit, string list> =
        let issues = [ checkCuesFront path ] |> List.choose id
        if issues.IsEmpty then Ok() else Error issues
