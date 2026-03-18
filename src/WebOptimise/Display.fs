namespace WebOptimise

open System.IO
open System.Threading
open System.Threading.Tasks
open Spectre.Console

/// Spectre.Console display functions for CLI output.
[<RequireQualifiedAccess>]
module Display =

    let printError (msg: string) =
        AnsiConsole.MarkupLine $"[red]Error:[/] %s{Markup.Escape msg}"

    let printStyled (style: string) (msg: string) =
        AnsiConsole.MarkupLine $"[%s{style}]%s{Markup.Escape msg}[/]"

    let displayAnalysis (infos: MediaFileInfo list) (userMode: Mode) =
        let table = Table()
        table.Title <- TableTitle "File Analysis"
        table.AddColumn "File" |> ignore
        table.AddColumn "Output Preview" |> ignore
        table.AddColumn(TableColumn("Duration").RightAligned()) |> ignore
        table.AddColumn(TableColumn("Size").RightAligned()) |> ignore
        table.AddColumn "Resolution" |> ignore
        table.AddColumn "Codec" |> ignore
        table.AddColumn(TableColumn("Video Bitrate").RightAligned()) |> ignore

        for info in infos do
            let fileMode = Discovery.effectiveMode info userMode
            let config = ModeConfig.forMode fileMode

            table.AddRow(
                Markup.Escape(MediaFilePath.name info.Path),
                $"[dim]%s{Markup.Escape(Discovery.sanitiseFilename (MediaFilePath.name info.Path) config.OutputExt)}[/]",
                MediaFileInfo.durationDisplay info,
                $"%.1f{MediaFileInfo.sizeMB info} MB",
                VideoStream.resolutionLabel info.Video,
                $"%s{info.Video.Codec} (%s{info.Video.Profile})",
                VideoStream.bitrateKbps info.Video
            )
            |> ignore

        AnsiConsole.Write table

    let displayRemuxWarnings (infos: MediaFileInfo list) =
        let warnings =
            infos
            |> List.collect (fun info ->
                if Constants.mkvExtensions.Contains(MediaFilePath.extension info.Path) then
                    []
                else
                    let name = MediaFilePath.name info.Path

                    [ if info.Video.Codec <> "h264" then
                          $"  [yellow]![/] %s{Markup.Escape name}: codec is %s{info.Video.Codec} (not H.264). Use --mode encode to re-encode."
                      elif not (info.Video.Profile.Contains "High") then
                          $"  [yellow]![/] %s{Markup.Escape name}: profile is %s{info.Video.Profile} (not High). Use --mode encode to upgrade." ])

        if not warnings.IsEmpty then
            AnsiConsole.WriteLine()

            for w in warnings do
                AnsiConsole.MarkupLine w

    let printSummary (results: EncodeResult list) =
        let table = Table()
        table.Title <- TableTitle "Encoding Results"
        table.AddColumn(TableColumn("File").NoWrap()) |> ignore
        table.AddColumn(TableColumn("Output").NoWrap()) |> ignore
        table.AddColumn(TableColumn("Input Size").RightAligned()) |> ignore
        table.AddColumn(TableColumn("Output Size").RightAligned()) |> ignore
        table.AddColumn(TableColumn("Change").RightAligned()) |> ignore

        let mutable totalInput = 0L
        let mutable totalOutput = 0L

        for r in results do
            totalInput <- totalInput + r.InputSize
            totalOutput <- totalOutput + r.OutputSize
            let inputMb = float r.InputSize / float Constants.BytesPerMB
            let outputMb = float r.OutputSize / float Constants.BytesPerMB
            let style = if EncodeResult.savingsPct r < 0.0 then "green" else "red"

            table.AddRow(
                Markup.Escape(MediaFilePath.name r.InputPath),
                Markup.Escape(Path.GetFileName r.OutputPath |> NullSafe.path),
                $"%.1f{inputMb} MB",
                $"%.1f{outputMb} MB",
                $"[%s{style}]%s{EncodeResult.savingsDisplay r}[/%s{style}]"
            )
            |> ignore

        if totalInput > 0L then
            let totalInputMb = float totalInput / float Constants.BytesPerMB
            let totalOutputMb = float totalOutput / float Constants.BytesPerMB
            let totalPct = float (totalOutput - totalInput) / float totalInput * 100.0
            let sign = if totalPct > 0.0 then "+" else ""
            let style = if totalPct < 0.0 then "green" else "red"

            table.AddRow(
                "[bold]Total[/]",
                "",
                $"[bold]%.1f{totalInputMb} MB[/]",
                $"[bold]%.1f{totalOutputMb} MB[/]",
                $"[bold][%s{style}]%s{sign}%.1f{totalPct}%%[/%s{style}][/]"
            )
            |> ignore

        AnsiConsole.Write table

    let displayVerification (results: (EncodeResult * Mode) list) : bool =
        AnsiConsole.MarkupLine "\n[bold]Verifying outputs...[/]"
        let mutable allOk = true

        for result, fileMode in results do
            let config = ModeConfig.forMode fileMode
            let verifyResult = config.Verifier result.OutputPath

            match verifyResult with
            | Ok() ->
                AnsiConsole.MarkupLine
                    $"  [green]\u2713[/] %s{Markup.Escape(Path.GetFileName result.OutputPath |> NullSafe.path)}"
            | Error issues ->
                allOk <- false

                AnsiConsole.MarkupLine
                    $"  [red]\u2717[/] %s{Markup.Escape(Path.GetFileName result.OutputPath |> NullSafe.path)}"

                for issue in issues do
                    AnsiConsole.MarkupLine $"    [red]\u2192[/] %s{Markup.Escape issue}"

        allOk

    let withProgress
        (infos: MediaFileInfo list)
        (processOne:
            MediaFileInfo -> Mode -> (float -> unit) -> CancellationToken -> Task<Result<EncodeResult, AppError>>)
        (userMode: Mode)
        (ct: CancellationToken)
        : Task<Result<ProcessResult list, AppError>> =
        task {
            let mutable results: ProcessResult list = []
            let mutable error: AppError voption = ValueNone

            do!
                AnsiConsole
                    .Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(
                        SpinnerColumn(),
                        TaskDescriptionColumn(),
                        ProgressBarColumn(),
                        PercentageColumn(),
                        ElapsedTimeColumn(),
                        RemainingTimeColumn()
                    )
                    .StartAsync(fun ctx ->
                        task {
                            for idx in 0 .. infos.Length - 1 do
                                if error.IsNone then
                                    let info = infos[idx]
                                    let fileMode = Discovery.effectiveMode info userMode

                                    let description =
                                        $"[%d{idx + 1}/%d{infos.Length}] %s{MediaFilePath.name info.Path}"

                                    let progressTask = ctx.AddTask(description, maxValue = info.DurationSecs)

                                    let onProgress elapsed = progressTask.Value <- elapsed

                                    match! processOne info fileMode onProgress ct with
                                    | Ok encodeResult ->
                                        progressTask.Value <- info.DurationSecs
                                        results <- results @ [ (encodeResult, fileMode) ]
                                    | Error e -> error <- ValueSome e
                        })

            match error with
            | ValueSome e -> return Error e
            | ValueNone -> return Ok results
        }
