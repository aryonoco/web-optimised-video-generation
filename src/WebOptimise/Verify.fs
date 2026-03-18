namespace WebOptimise

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Verify =

    let private runFfprobe (args: string list) : Task<Result<BufferedOutput, string>> = Shell.runBuffered "ffprobe" args

    let private toValidation (check: Task<string option>) : Task<Result<unit, string list>> =
        task {
            match! check with
            | None -> return Ok()
            | Some msg -> return Error [ msg ]
        }

    let private checkVideoProfile (path: string) : Task<string option> =
        task {
            match! runFfprobe [ "-v"; "quiet"; "-print_format"; "json"; "-show_streams"; path ] with
            | Error msg -> return Some msg
            | Ok result ->
                try
                    let root = JsonDocument.Parse(result.StdOut).RootElement

                    let profile =
                        match root with
                        | Json.Prop "streams" (Json.Arr streams) ->
                            streams
                            |> Seq.tryFind (fun s ->
                                match s with
                                | Json.Prop "codec_type" (Json.Str "video") -> true
                                | _ -> false)
                            |> Option.bind (fun s ->
                                match s with
                                | Json.Prop "profile" (Json.Str p) -> Some p
                                | _ -> None)
                        | _ -> None

                    match profile with
                    | None -> return Some "No video stream found"
                    | Some p when not (p.Contains("High", StringComparison.Ordinal)) ->
                        return Some $"Expected High profile, got '%s{p}'"
                    | _ -> return None
                with ex ->
                    return Some $"Failed to parse ffprobe output: %s{ex.Message}"
        }

    let private checkFaststart (path: string) : Task<string option> =
        task {
            match! runFfprobe [ "-v"; "trace"; path ] with
            | Error msg -> return Some msg
            | Ok result ->
                let stderr = result.StdErr
                let moovPos = stderr.IndexOf("type:'moov'", StringComparison.Ordinal)
                let mdatPos = stderr.IndexOf("type:'mdat'", StringComparison.Ordinal)

                if moovPos = -1 || mdatPos = -1 then
                    return Some "Could not determine moov/mdat positions"
                elif moovPos > mdatPos then
                    return Some "faststart not applied: moov atom is after mdat"
                else
                    return None
        }

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

    let private analyseKeyframeIntervals (output: string) : string option =
        let keyframeTimes = parseKeyframeTimes output

        if keyframeTimes.Length >= Constants.MinKeyframesForCheck then
            let sampleCount = min (keyframeTimes.Length - 1) Constants.MaxKeyframeSample

            let intervals =
                [ for i in 0 .. sampleCount - 1 -> keyframeTimes[i + 1] - keyframeTimes[i] ]

            let avgInterval = List.average intervals

            if avgInterval > Constants.MaxAcceptableKeyframeInterval then
                Some $"Keyframe interval too large: %.1f{avgInterval}s (expected ~%d{Constants.KeyframeIntervalSecs}s)"
            else
                None
        else
            None

    let private checkKeyframeIntervals (path: string) : Task<string option> =
        task {
            let! probeResult =
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

            return
                match probeResult with
                | Error msg -> Some msg
                | Ok result -> analyseKeyframeIntervals result.StdOut
        }

    let private checkCuesFront (path: string) : Task<string option> =
        let result =
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

        Task.FromResult result

    let verifyEncoded (path: string) : Task<Result<unit, string list>> =
        taskValidation {
            let! _ = toValidation (checkVideoProfile path)
            and! _ = toValidation (checkFaststart path)
            and! _ = toValidation (checkKeyframeIntervals path)
            return ()
        }

    let verifyRemuxed (path: string) : Task<Result<unit, string list>> = toValidation (checkFaststart path)

    let verifyWebm (path: string) : Task<Result<unit, string list>> = toValidation (checkCuesFront path)
