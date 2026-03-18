namespace WebOptimise

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Verify =

    let toValidation (check: Task<VerificationIssue voption>) : Task<Result<unit, VerificationIssue list>> =
        task {
            match! check with
            | ValueNone -> return Ok()
            | ValueSome issue -> return Error [ issue ]
        }

    // Pure validators

    let validateVideoProfile (json: string) : VerificationIssue voption =
        try
            let root = JsonElement.Parse(json)

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
            | None -> ValueSome VerificationIssue.NoVideoStream
            | Some VideoProfile.High -> ValueNone
            | Some other -> ValueSome(VerificationIssue.ProfileMismatch("High", VideoProfile.displayName other))
        with ex ->
            ValueSome(VerificationIssue.CheckFailed $"Failed to parse ffprobe output: %s{ex.Message}")

    let validateFaststart (stderr: string) : VerificationIssue voption =
        let moovPos =
            stderr.IndexOf("type:'moov'", StringComparison.Ordinal)

        let mdatPos =
            stderr.IndexOf("type:'mdat'", StringComparison.Ordinal)

        if moovPos = -1 || mdatPos = -1 then
            ValueSome VerificationIssue.AtomPositionUnknown
        elif moovPos > mdatPos then
            ValueSome VerificationIssue.FaststartMissing
        else
            ValueNone

    let validateCuesFront (data: ReadOnlySpan<byte>) : VerificationIssue voption =
        match Ebml.checkCuesBeforeCluster data with
        | Ok() -> ValueNone
        | Error msg -> ValueSome(VerificationIssue.CuesNotFront msg)

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

    let analyseKeyframeIntervals (output: string) : VerificationIssue voption =
        let keyframeTimes = parseKeyframeTimes output

        if keyframeTimes.Length >= Constants.MinKeyframesForCheck then
            let intervals =
                keyframeTimes
                |> List.pairwise
                |> List.truncate Constants.MaxKeyframeSample
                |> List.map (fun (a, b) -> b - a)

            match intervals with
            | [] -> ValueNone
            | _ ->
                let avgInterval = List.average intervals

                if avgInterval > Constants.MaxAcceptableKeyframeInterval then
                    ValueSome(VerificationIssue.KeyframeIntervalTooLarge(avgInterval, Constants.KeyframeIntervalSecs))
                else
                    ValueNone
        else
            ValueNone

    // I/O functions delegating to pure validators

    let checkVideoProfile (env: Env) (path: string) : Task<VerificationIssue voption> =
        task {
            match!
                env.RunBuffered "ffprobe" [
                    "-v"
                    "quiet"
                    "-print_format"
                    "json"
                    "-show_streams"
                    path
                ]
            with
            | Error e -> return ValueSome(VerificationIssue.CheckFailed(ShellError.format e))
            | Ok result -> return validateVideoProfile result.StdOut
        }

    let checkFaststart (env: Env) (path: string) : Task<VerificationIssue voption> =
        task {
            match!
                env.RunBuffered "ffprobe" [
                    "-v"
                    "trace"
                    path
                ]
            with
            | Error e -> return ValueSome(VerificationIssue.CheckFailed(ShellError.format e))
            | Ok result -> return validateFaststart result.StdErr
        }

    let checkKeyframeIntervals (env: Env) (path: string) : Task<VerificationIssue voption> =
        task {
            match!
                env.RunBuffered "ffprobe" [
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
            with
            | Error e -> return ValueSome(VerificationIssue.CheckFailed(ShellError.format e))
            | Ok result -> return analyseKeyframeIntervals result.StdOut
        }

    let checkCuesFront (path: string) : Task<VerificationIssue voption> =
        let result =
            try
                let data =
                    use fs = File.OpenRead(path)

                    let buf =
                        Array.zeroCreate (min (int fs.Length) Constants.HeaderScanSize)

                    let bytesRead = fs.Read(buf, 0, buf.Length)
                    ReadOnlySpan(buf, 0, bytesRead)

                validateCuesFront data
            with ex ->
                ValueSome(VerificationIssue.FileReadFailed ex.Message)

        Task.FromResult result

    // Composite verifiers

    let verifyEncoded (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkVideoProfile env p)
            and! _ = toValidation (checkFaststart env p)
            and! _ = toValidation (checkKeyframeIntervals env p)
            return ()
        }

    let verifyRemuxed (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkFaststart env p)
            return ()
        }

    let verifyWebm (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        ignore env
        let p = OutputPath.value path

        taskValidation {
            let! _ = toValidation (checkCuesFront p)
            return ()
        }
