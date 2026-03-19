namespace WebOptimise

open System
open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Verify =

    // Pure validators

    let validateVideoProfile (root: JsonElement) : Result<unit, VerificationIssue> =
        let profile =
            match root with
            | Json.Prop "streams" (Json.Arr streams) ->
                streams
                |> Seq.tryFind (fun s ->
                    match s with
                    | Json.Prop "codec_type" (Json.Str "video") -> true
                    | _ -> false
                )
                |> ValueOption.ofOption
                |> ValueOption.bind (fun s ->
                    match s with
                    | Json.Prop "profile" (Json.Str p) -> ValueSome(VideoProfile.ofString p)
                    | _ -> ValueNone
                )
            | _ -> ValueNone

        match profile with
        | ValueNone -> Error VerificationIssue.NoVideoStream
        | ValueSome VideoProfile.High -> Ok()
        | ValueSome other -> Error(VerificationIssue.ProfileMismatch("High", VideoProfile.displayName other))

    let validateFaststart (stderr: string) : Result<unit, VerificationIssue> =
        let moovPos =
            stderr.IndexOf("type:'moov'", StringComparison.Ordinal)

        let mdatPos =
            stderr.IndexOf("type:'mdat'", StringComparison.Ordinal)

        if moovPos = -1 || mdatPos = -1 then
            Error VerificationIssue.AtomPositionUnknown
        elif moovPos > mdatPos then
            Error VerificationIssue.FaststartMissing
        else
            Ok()

    let validateCuesFront (data: ReadOnlySpan<byte>) : Result<unit, VerificationIssue> =
        match Ebml.checkCuesBeforeCluster data with
        | Ok() -> Ok()
        | Error msg -> Error(VerificationIssue.CuesNotFront msg)

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

    let analyseKeyframeIntervals (output: string) : Result<unit, VerificationIssue> =
        let keyframeTimes = parseKeyframeTimes output

        if keyframeTimes.Length >= Constants.MinKeyframesForCheck then
            let intervals =
                keyframeTimes
                |> List.pairwise
                |> List.truncate Constants.MaxKeyframeSample
                |> List.map (fun (a, b) -> b - a)

            match intervals with
            | [] -> Ok()
            | _ ->
                let sum = List.fold (+) 0.0 intervals
                let avgInterval = sum / float intervals.Length

                if avgInterval > Constants.MaxAcceptableKeyframeInterval then
                    Error(VerificationIssue.KeyframeIntervalTooLarge(avgInterval, Constants.KeyframeIntervalSecs))
                else
                    Ok()
        else
            Ok()

    // I/O functions delegating to pure validators

    let checkVideoProfile (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        task {
            match!
                env.RunBuffered "ffprobe" [
                    "-v"
                    "quiet"
                    "-print_format"
                    "json"
                    "-show_streams"
                    OutputPath.value path
                ]
            with
            | Error e -> return Error [ VerificationIssue.CheckFailed(ShellError.format e) ]
            | Ok result ->
                match Json.parse result.StdOut with
                | Error msg -> return Error [ VerificationIssue.CheckFailed $"Failed to parse ffprobe output: %s{msg}" ]
                | Ok root -> return validateVideoProfile root |> Result.mapError List.singleton
        }

    let checkFaststart (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        task {
            match!
                env.RunBuffered "ffprobe" [
                    "-v"
                    "trace"
                    OutputPath.value path
                ]
            with
            | Error e -> return Error [ VerificationIssue.CheckFailed(ShellError.format e) ]
            | Ok result ->
                return
                    validateFaststart result.StdErr
                    |> Result.mapError List.singleton
        }

    let checkKeyframeIntervals (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
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
                    OutputPath.value path
                ]
            with
            | Error e -> return Error [ VerificationIssue.CheckFailed(ShellError.format e) ]
            | Ok result ->
                return
                    analyseKeyframeIntervals result.StdOut
                    |> Result.mapError List.singleton
        }

    let checkCuesFront (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        match env.ReadFileHeader path Constants.HeaderScanSize with
        | Error e -> Task.FromResult(Error [ VerificationIssue.FileReadFailed(ShellError.format e) ])
        | Ok buf ->
            let result =
                validateCuesFront (ReadOnlySpan(buf, 0, buf.Length))
                |> Result.mapError List.singleton

            Task.FromResult result

    let verifyEncoded (env: Env) (path: OutputPath) : Task<Result<unit, VerificationIssue list>> =
        taskValidation {
            let! _ = checkVideoProfile env path
            and! _ = checkFaststart env path
            and! _ = checkKeyframeIntervals env path
            return ()
        }
