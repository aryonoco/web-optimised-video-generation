namespace WebOptimise

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Verify =

    let runFfprobe (args: string list) : Task<Result<BufferedOutput, ShellError>> = Shell.runBuffered "ffprobe" args

    let toValidation (check: Task<string option>) : Task<Result<unit, string list>> =
        task {
            match! check with
            | None -> return Ok()
            | Some msg -> return Error [ msg ]
        }

    let checkVideoProfile (path: string) : Task<string option> =
        task {
            match!
                runFfprobe [
                    "-v"
                    "quiet"
                    "-print_format"
                    "json"
                    "-show_streams"
                    path
                ]
            with
            | Error e -> return Some(ShellError.format e)
            | Ok result ->
                try
                    let root = JsonElement.Parse(result.StdOut)

                    let profile =
                        match root with
                        | Json.Prop "streams" (Json.Arr streams) ->
                            streams
                            |> Seq.tryFind (fun s ->
                                match s with
                                | Json.Prop "codec_type" (Json.Str "video") -> true
                                | _ -> false
                            )
                            |> Option.bind (fun s ->
                                match s with
                                | Json.Prop "profile" (Json.Str p) -> Some(VideoProfile.ofString p)
                                | _ -> None
                            )
                        | _ -> None

                    match profile with
                    | None -> return Some "No video stream found"
                    | Some VideoProfile.High -> return None
                    | Some other -> return Some $"Expected High profile, got '%s{VideoProfile.displayName other}'"
                with ex ->
                    return Some $"Failed to parse ffprobe output: %s{ex.Message}"
        }

    let checkFaststart (path: string) : Task<string option> =
        task {
            match!
                runFfprobe [
                    "-v"
                    "trace"
                    path
                ]
            with
            | Error e -> return Some(ShellError.format e)
            | Ok result ->
                let stderr = result.StdErr

                let moovPos =
                    stderr.IndexOf("type:'moov'", StringComparison.Ordinal)

                let mdatPos =
                    stderr.IndexOf("type:'mdat'", StringComparison.Ordinal)

                if moovPos = -1 || mdatPos = -1 then
                    return Some "Could not determine moov/mdat positions"
                elif moovPos > mdatPos then
                    return Some "faststart not applied: moov atom is after mdat"
                else
                    return None
        }

    let parseKeyframeTimes (output: string) : float list =
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            if line.Contains(",K", StringComparison.Ordinal) then
                let pts =
                    line.AsSpan().Slice(0, max 0 (line.IndexOf(','))).ToString()

                match Double.TryParse pts with
                | true, v -> Some v
                | _ -> None
            else
                None
        )
        |> Array.toList

    let analyseKeyframeIntervals (output: string) : string option =
        let keyframeTimes = parseKeyframeTimes output

        if keyframeTimes.Length >= Constants.MinKeyframesForCheck then
            let intervals =
                keyframeTimes
                |> List.pairwise
                |> List.truncate Constants.MaxKeyframeSample
                |> List.map (fun (a, b) -> b - a)

            match intervals with
            | [] -> None
            | _ ->
                let avgInterval = List.average intervals

                if avgInterval > Constants.MaxAcceptableKeyframeInterval then
                    Some
                        $"Keyframe interval too large: %.1f{avgInterval}s (expected ~%d{Constants.KeyframeIntervalSecs}s)"
                else
                    None
        else
            None

    let checkKeyframeIntervals (path: string) : Task<string option> =
        task {
            let! probeResult =
                runFfprobe [
                    "-v"
                    "quiet"
                    "-select_streams"
                    "v:0"
                    "-show_entries"
                    "packet=pts_time,flags"
                    "-of"
                    "csv=p=0"
                    path
                ]

            return
                match probeResult with
                | Error e -> Some(ShellError.format e)
                | Ok result -> analyseKeyframeIntervals result.StdOut
        }

    let checkCuesFront (path: string) : Task<string option> =
        let result =
            try
                let data =
                    use fs = File.OpenRead(path)

                    let buf =
                        Array.zeroCreate (min (int fs.Length) Constants.HeaderScanSize)

                    let bytesRead = fs.Read(buf, 0, buf.Length)
                    ReadOnlySpan(buf, 0, bytesRead)

                match Ebml.checkCuesBeforeCluster data with
                | Ok() -> None
                | Error msg -> Some msg
            with ex ->
                Some $"Could not read file for verification: %s{ex.Message}"

        Task.FromResult result

    let verifyEncoded (path: OutputPath) : Task<Result<unit, string list>> =
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkVideoProfile p)
            and! _ = toValidation (checkFaststart p)
            and! _ = toValidation (checkKeyframeIntervals p)
            return ()
        }

    let verifyRemuxed (path: OutputPath) : Task<Result<unit, string list>> =
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkFaststart p)
            return ()
        }

    let verifyWebm (path: OutputPath) : Task<Result<unit, string list>> =
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkCuesFront p)
            return ()
        }
