namespace WebOptimise

open System
open System.Threading
open System.Threading.Tasks
open Argu
open FsToolkit.ErrorHandling
open SpectreCoff

type CliArgs =
    | [<MainCommand; ExactlyOnce; Last>] Paths of path: string list
    | [<AltCommandLine("-m")>] Mode of mode: string
    | [<AltCommandLine("-n")>] DryRun
    | [<AltCommandLine("-f")>] Overwrite

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Paths _ -> "media file(s) or directory(ies) to process"
            | Mode _ ->
                "processing mode for MP4/M4V/MOV: 'remux' (default) or 'encode'. MKV files are always remuxed to WebM."
            | DryRun -> "show what would be processed without encoding"
            | Overwrite -> "re-process even if output file already exists"

[<NoComparison; NoEquality>]
type private ValidatedInput = {
    Paths: NonEmpty<string>
    Mode: WebOptimise.Mode
    DryRun: bool
    Overwrite: bool
}

[<NoComparison; NoEquality>]
type private DiscoveredFiles = {
    Input: ValidatedInput
    Files: NonEmpty<MediaFilePath>
}

[<NoComparison; NoEquality>]
type private AnalysedPipeline = {
    Input: ValidatedInput
    Infos: NonEmpty<MediaFileInfo>
    Modes: Mode list
}

[<RequireQualifiedAccess>]
module Cli =

    let private parseMode (s: string) : Result<WebOptimise.Mode, AppError> =
        match s.ToLowerInvariant() with
        | "remux" -> Ok WebOptimise.Mode.Remux
        | "encode" -> Ok WebOptimise.Mode.Encode
        | other -> Error(AppError.Validation(ValidationFailure.UnknownMode other))

    let private parseAndValidate (argv: string array) : Result<ValidatedInput, AppError> =
        try
            let parser =
                ArgumentParser.Create<CliArgs>(
                    programName = "web-optimise",
                    helpTextMessage = "Optimise media files for progressive web delivery."
                )

            let results = parser.Parse(argv)

            let rawPaths = results.GetResult(Paths, defaultValue = [])
            let modeStr = results.GetResult(Mode, defaultValue = "remux")
            let dryRun = results.Contains DryRun
            let overwrite = results.Contains Overwrite

            result {
                let! mode = parseMode modeStr

                let! paths =
                    match NonEmpty.ofList rawPaths with
                    | ValueSome nel -> Ok nel
                    | ValueNone -> Error(AppError.Validation ValidationFailure.NoPaths)

                return {
                    Paths = paths
                    Mode = mode
                    DryRun = dryRun
                    Overwrite = overwrite
                }
            }
        with :? ArguParseException as ex ->
            Error(AppError.Validation(ValidationFailure.ArguError ex.Message))

    let private validateEnvironment (env: Env) : Task<Result<unit, AppError>> =
        taskResult {
            let! _ =
                env.RunExists "ffmpeg"
                |> TaskResult.mapError (fun _ -> AppError.Validation(ValidationFailure.ToolNotFound "ffmpeg"))

            and! _ =
                env.RunExists "ffprobe"
                |> TaskResult.mapError (fun _ -> AppError.Validation(ValidationFailure.ToolNotFound "ffprobe"))

            return ()
        }

    let private probeFile (env: Env) (path: MediaFilePath) : Task<Result<MediaFileInfo, AppError>> =
        task {
            match!
                env.RunBuffered "ffprobe" [
                    "-v"
                    "quiet"
                    "-print_format"
                    "json"
                    "-show_streams"
                    "-show_format"
                    MediaFilePath.value path
                ]
            with
            | Error e -> return Error(AppError.Probe(ProbeFailure.ShellFailed(e, path)))
            | Ok probeResult ->
                if probeResult.ExitCode <> 0 then
                    return
                        Error(AppError.Probe(ProbeFailure.NonZeroExit(probeResult.ExitCode, probeResult.StdErr, path)))
                else
                    match env.ParseJson probeResult.StdOut with
                    | Error msg -> return Error(AppError.Probe(ProbeFailure.JsonParseFailed(msg, path)))
                    | Ok root -> return ProbeParse.fromJson path root
        }

    let private discover (env: Env) (input: ValidatedInput) : Task<Result<DiscoveredFiles, AppError>> =
        taskResult {
            let resolved =
                input.Paths
                |> NonEmpty.toList
                |> List.map env.ResolveInputPath

            let! files = Discovery.collectFiles resolved
            do! Discovery.rejectMkvEncode (NonEmpty.toList files) input.Mode

            return {
                Input = input
                Files = files
            }
        }

    let private analyse (env: Env) (discovered: DiscoveredFiles) : Task<Result<AnalysedPipeline, AppError>> =
        taskResult {
            let! infos =
                discovered.Files
                |> NonEmpty.traverseTaskResultA (probeFile env)
                |> TaskResult.mapError (fun errors ->
                    match NonEmpty.length errors with
                    | 1 -> NonEmpty.head errors
                    | _ -> AppError.Multiple errors
                )

            do! Discovery.validateMkvCodecs (NonEmpty.toList infos)

            let effectiveModes =
                infos
                |> NonEmpty.toList
                |> List.map (fun i -> Discovery.effectiveMode i discovered.Input.Mode)
                |> List.distinct

            return {
                Input = discovered.Input
                Infos = infos
                Modes = effectiveModes
            }
        }

    let private processAndVerifyOne
        (env: Env)
        (overwrite: bool)
        (userMode: WebOptimise.Mode)
        (info: MediaFileInfo)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<PipelineResult, AppError>>
        =
        task {
            let fileMode = Discovery.effectiveMode info userMode
            let outputDir = Discovery.outputDir info

            match! Process.processFile env info outputDir fileMode overwrite onProgress ct with
            | Error e -> return Error e
            | Ok encodeResult ->
                let config = ModeConfig.forMode fileMode
                let! verification = config.Verifier env encodeResult.OutputPath

                return
                    Ok {
                        Encode = encodeResult
                        Mode = fileMode
                        Verification = verification
                    }
        }

    let private execute (env: Env) (pipeline: AnalysedPipeline) : Task<int> =
        task {
            use cts = new CancellationTokenSource()

            Console.CancelKeyPress.Add(fun e ->
                e.Cancel <- true
                cts.Cancel()
            )

            let infos = NonEmpty.toList pipeline.Infos

            let! pipelineResult =
                Display.withProgress
                    infos
                    (processAndVerifyOne env pipeline.Input.Overwrite pipeline.Input.Mode)
                    cts.Token

            match pipelineResult with
            | Error e ->
                Display.printError (AppError.format e)
                return 1
            | Ok results ->
                let allOk = Display.displayVerification results

                NL |> toConsole
                Display.printSummary (results |> List.map _.Encode)

                let verb =
                    results
                    |> List.map _.Mode
                    |> List.distinct
                    |> List.map ModeConfig.completionVerb
                    |> List.distinct
                    |> function
                        | [ single ] -> single
                        | _ -> "processed"

                if allOk then
                    P $"Done! %d{results.Length} file(s) %s{verb}." |> toConsole
                else
                    E "Done with warnings. Check verification results above."
                    |> toConsole

                return 0
        }

    let run (argv: string array) : Task<int> =
        let env = Env.live

        task {
            match!
                taskResult {
                    let! input = parseAndValidate argv
                    do! validateEnvironment env
                    let! discovered = discover env input
                    return! analyse env discovered
                }
            with
            | Error e ->
                Display.printError (AppError.format e)
                return 1
            | Ok pipeline ->
                let infos = NonEmpty.toList pipeline.Infos

                let modeLabel =
                    pipeline.Modes
                    |> List.map ModeConfig.label
                    |> List.sort
                    |> String.concat " + "

                P $"Found %d{NonEmpty.length pipeline.Infos} file(s) to process (mode: %s{modeLabel})"
                |> toConsole

                Display.displayAnalysis infos pipeline.Input.Mode

                if pipeline.Input.Mode = WebOptimise.Mode.Remux then
                    Display.displayRemuxWarnings infos

                if pipeline.Input.DryRun then
                    let dryVerb =
                        pipeline.Modes
                        |> List.map ModeConfig.completionVerb
                        |> List.distinct
                        |> function
                            | [ single ] -> single
                            | _ -> "processed"

                    E $"Dry run \u2014 no files were %s{dryVerb}." |> toConsole
                    return 0
                else
                    return! execute env pipeline
        }
