namespace WebOptimise

open System
open System.Threading
open System.Threading.Tasks
open Argu
open FsToolkit.ErrorHandling
open CliWrap
open CliWrap.Buffered
open Spectre.Console

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
type ParsedArgs =
    { Paths: string list
      Mode: WebOptimise.Mode
      DryRun: bool
      Overwrite: bool }

[<RequireQualifiedAccess>]
module Cli =

    let private parseMode (s: string) : Result<WebOptimise.Mode, AppError> =
        match s.ToLowerInvariant() with
        | "remux" -> Ok WebOptimise.Mode.Remux
        | "encode" -> Ok WebOptimise.Mode.Encode
        | other -> Error(AppError.ValidationError $"Unknown mode: '%s{other}'. Use 'remux' or 'encode'.")

    let private parseArgs (argv: string array) : Result<ParsedArgs, AppError> =
        try
            let parser =
                ArgumentParser.Create<CliArgs>(
                    programName = "web-optimise",
                    helpTextMessage = "Optimise media files for progressive web delivery.",
                    errorHandler = ProcessExiter()
                )

            let results = parser.Parse(argv, raiseOnUsage = true)

            let paths = results.GetResult(Paths, defaultValue = [])
            let modeStr = results.GetResult(Mode, defaultValue = "remux")
            let dryRun = results.Contains DryRun
            let overwrite = results.Contains Overwrite

            result {
                let! mode = parseMode modeStr

                if paths.IsEmpty then
                    return! Error(AppError.ValidationError "No paths provided.")

                return
                    { Paths = paths
                      Mode = mode
                      DryRun = dryRun
                      Overwrite = overwrite }
            }
        with :? ArguParseException as ex ->
            Error(AppError.ValidationError ex.Message)

    let private validateEnvironment () : Task<Result<unit, AppError>> =
        let check (tool: string) : Task<Result<unit, AppError>> =
            task {
                try
                    let! _ = Cli.Wrap(tool).WithArguments([ "-version" ]).ExecuteBufferedAsync()
                    return Ok()
                with
                | :? System.ComponentModel.Win32Exception ->
                    return Error(AppError.ValidationError $"%s{tool} not found in PATH")
                | ex -> return Error(AppError.ValidationError $"%s{tool} failed to run: %s{ex.Message}")
            }

        taskResult {
            do! check "ffmpeg"
            do! check "ffprobe"
        }

    let private probeFile (path: MediaFilePath) : Task<Result<MediaFileInfo, AppError>> =
        task {
            try
                let! probeResult =
                    CliWrap.Cli
                        .Wrap("ffprobe")
                        .WithArguments(
                            [ "-v"
                              "quiet"
                              "-print_format"
                              "json"
                              "-show_streams"
                              "-show_format"
                              MediaFilePath.value path ]
                        )
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteBufferedAsync()

                if probeResult.ExitCode <> 0 then
                    return
                        Error(
                            AppError.ProbeError
                                $"ffprobe failed for %s{MediaFilePath.name path}: %s{probeResult.StandardError}"
                        )
                else
                    return ProbeParse.fromJson path probeResult.StandardOutput
            with ex ->
                return Error(AppError.ProbeError $"ffprobe failed for %s{MediaFilePath.name path}: %s{ex.Message}")
        }

    let private processOne
        (args: ParsedArgs)
        (info: MediaFileInfo)
        (fileMode: WebOptimise.Mode)
        (onProgress: float -> unit)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>> =
        let outputDir = Discovery.outputDir info
        Process.processFile info outputDir fileMode args.Overwrite onProgress ct

    let run (argv: string array) : Task<int> =
        task {
            let pipeline =
                taskResult {
                    let! args = parseArgs argv
                    do! validateEnvironment ()
                    let! files = Discovery.findFiles args.Paths

                    if files.IsEmpty then
                        AnsiConsole.MarkupLine "[yellow]No supported media files found.[/]"
                        return None
                    else

                        do! Discovery.rejectMkvEncode files args.Mode

                        let! infos = files |> List.traverseTaskResultM probeFile

                        do! Discovery.validateMkvCodecs infos

                        let effectiveModes =
                            infos
                            |> List.map (fun i -> Discovery.effectiveMode i args.Mode)
                            |> List.distinct

                        let modeLabel =
                            effectiveModes |> List.map ModeConfig.label |> List.sort |> String.concat " + "

                        AnsiConsole.MarkupLine
                            $"\n[bold]Found %d{infos.Length} file(s) to process[/] (mode: %s{modeLabel})\n"

                        Display.displayAnalysis infos args.Mode

                        if args.Mode = WebOptimise.Mode.Remux then
                            Display.displayRemuxWarnings infos

                        if args.DryRun then
                            let dryVerbs = effectiveModes |> List.map ModeConfig.completionVerb |> List.distinct

                            let dryVerb = if dryVerbs.Length = 1 then dryVerbs.Head else "processed"

                            AnsiConsole.MarkupLine $"\n[yellow]Dry run \u2014 no files were %s{dryVerb}.[/]"
                            return None
                        else

                            return Some(args, infos, effectiveModes)
                }

            let! pipelineResult = pipeline

            match pipelineResult with
            | Error e ->
                Display.printError (AppError.message e)
                return 1
            | Ok None -> return 0
            | Ok(Some(args, infos, effectiveModes)) ->
                use cts = new CancellationTokenSource()

                Console.CancelKeyPress.Add(fun e ->
                    e.Cancel <- true
                    cts.Cancel())

                let! progressResult = Display.withProgress infos (processOne args) args.Mode cts.Token

                match progressResult with
                | Error e ->
                    Display.printError (AppError.message e)
                    return 1
                | Ok resultsWithModes ->
                    let! allOk = Display.displayVerification resultsWithModes

                    AnsiConsole.WriteLine()
                    Display.printSummary (resultsWithModes |> List.map fst)

                    if allOk then
                        let verbs = effectiveModes |> List.map ModeConfig.completionVerb |> List.distinct

                        let verb = if verbs.Length = 1 then verbs.Head else "processed"

                        AnsiConsole.MarkupLine
                            $"\n[bold green]Done![/bold green] %d{resultsWithModes.Length} file(s) %s{verb}."
                    else
                        AnsiConsole.MarkupLine
                            "\n[bold yellow]Done with warnings.[/bold yellow] Check verification results above."

                    return 0
        }
