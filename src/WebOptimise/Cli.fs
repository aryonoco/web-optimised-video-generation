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
                    return ProbeParse.fromJson path probeResult.StdOut
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
                |> NonEmpty.toList
                |> List.traverseTaskResultA (probeFile env)
                |> TaskResult.mapError (fun errors ->
                    match errors with
                    | [ single ] -> single
                    | h :: t -> AppError.Multiple(NonEmpty(h, t))
                    | [] -> AppError.Validation ValidationFailure.NoPaths
                )

            do! Discovery.validateMkvCodecs infos

            let effectiveModes =
                infos
                |> List.map (fun i -> Discovery.effectiveMode i discovered.Input.Mode)
                |> List.distinct

            let infosNel =
                match NonEmpty.ofList infos with
                | ValueSome nel -> nel
                | ValueNone -> NonEmpty.singleton (List.head infos)

            return {
                Input = discovered.Input
                Infos = infosNel
                Modes = effectiveModes
            }
        }

    let private processOne
        (env: Env)
        (overwrite: bool)
        (info: MediaFileInfo)
        (fileMode: WebOptimise.Mode)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        let outputDir = Discovery.outputDir info
        Process.processFile env info outputDir fileMode overwrite onProgress ct

    let private execute (env: Env) (pipeline: AnalysedPipeline) : Task<int> =
        task {
            use cts = new CancellationTokenSource()

            Console.CancelKeyPress.Add(fun e ->
                e.Cancel <- true
                cts.Cancel()
            )

            let infos = NonEmpty.toList pipeline.Infos

            let! progressResult =
                Display.withProgress infos (processOne env pipeline.Input.Overwrite) pipeline.Input.Mode cts.Token

            match progressResult with
            | Error e ->
                Display.printError (AppError.format e)
                return 1
            | Ok resultsWithModes ->
                let! verificationResults =
                    resultsWithModes
                    |> List.map (fun pr ->
                        task {
                            let config = ModeConfig.forMode pr.Mode
                            let! vr = config.Verifier env pr.Result.OutputPath

                            return {
                                FileName = OutputPath.fileName pr.Result.OutputPath
                                Outcome = vr
                            }
                        }
                    )
                    |> Task.WhenAll

                let allOk =
                    Display.displayVerification (Array.toList verificationResults)

                NL |> toConsole
                Display.printSummary (resultsWithModes |> List.map _.Result)

                let verb =
                    pipeline.Modes
                    |> List.map ModeConfig.completionVerb
                    |> List.distinct
                    |> function
                        | [ single ] -> single
                        | _ -> "processed"

                if allOk then
                    P $"Done! %d{resultsWithModes.Length} file(s) %s{verb}."
                    |> toConsole
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
