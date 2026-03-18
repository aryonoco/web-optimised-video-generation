namespace WebOptimise

open System
open System.Threading
open System.Threading.Tasks
open Spectre.Console
open SpectreCoff

/// SpectreCoff display functions for CLI output.
[<RequireQualifiedAccess>]
module Display =

    let printError (msg: string) =
        Many [
            MC(Color.Red, "Error:")
            V $" %s{msg}"
        ]
        |> toConsole

    let displayAnalysis (infos: MediaFileInfo list) (userMode: Mode) =
        let rightAligned = { defaultColumnLayout with Alignment = Right }

        let columns = [
            column (C "File")
            column (C "Output Preview")
            column (C "Duration") |> withLayout rightAligned
            column (C "Size") |> withLayout rightAligned
            column (C "Resolution")
            column (C "Codec")
            column (C "Video Bitrate") |> withLayout rightAligned
        ]

        let rows =
            infos
            |> List.map (fun info ->
                let fileMode = Discovery.effectiveMode info userMode
                let config = ModeConfig.forMode fileMode

                Payloads [
                    V(MediaFilePath.name info.Path)
                    C(Discovery.sanitiseFilename (Guid.NewGuid()) (MediaFilePath.name info.Path) config.OutputExt)
                    V(MediaFileInfo.durationDisplay info)
                    V $"%.1f{MediaFileInfo.sizeMB info} MB"
                    V(VideoStream.resolutionLabel info.Video)
                    V(
                        VideoCodec.displayName info.Video.Codec
                        + " ("
                        + (
                            match info.Video.Profile with
                            | ValueSome p -> VideoProfile.displayName p
                            | ValueNone -> "N/A"
                        )
                        + ")"
                    )
                    V(VideoStream.bitrateKbps info.Video)
                ]
            )

        table columns rows
        |> withTitle "File Analysis"
        |> toOutputPayload
        |> toConsole

    let displayRemuxWarnings (infos: MediaFileInfo list) =
        let warnings =
            infos
            |> List.collect (fun info ->
                if Constants.mkvExtensions.Contains(MediaFilePath.extension info.Path) then
                    []
                else
                    let name = MediaFilePath.name info.Path

                    [
                        if info.Video.Codec <> VideoCodec.H264 then
                            Many [
                                MC(Color.Yellow, "!")
                                V
                                    $" %s{name}: codec is %s{VideoCodec.displayName info.Video.Codec} (not H.264). Use --mode encode to re-encode."
                            ]
                        elif info.Video.Profile <> ValueSome VideoProfile.High then
                            let profileLabel =
                                match info.Video.Profile with
                                | ValueSome p -> VideoProfile.displayName p
                                | ValueNone -> "N/A"

                            Many [
                                MC(Color.Yellow, "!")
                                V $" %s{name}: profile is %s{profileLabel} (not High). Use --mode encode to upgrade."
                            ]
                    ]
            )

        if not warnings.IsEmpty then
            NL |> toConsole
            warnings |> List.iter toConsole

    let printSummary (results: EncodeResult list) =
        let rightAligned = { defaultColumnLayout with Alignment = Right }

        let noWrap = { defaultColumnLayout with Wrap = false }

        let columns = [
            column (C "File") |> withLayout noWrap
            column (C "Output") |> withLayout noWrap
            column (C "Input Size") |> withLayout rightAligned
            column (C "Output Size") |> withLayout rightAligned
            column (C "Change") |> withLayout rightAligned
        ]

        let dataRows =
            results
            |> List.map (fun r ->
                let inputMb = float r.InputSize / float Constants.BytesPerMB
                let outputMb = float r.OutputSize / float Constants.BytesPerMB

                let color =
                    if EncodeResult.savingsPct r < 0.0 then
                        Color.Green
                    else
                        Color.Red

                Payloads [
                    V(MediaFilePath.name r.InputPath)
                    V(OutputPath.fileName r.OutputPath)
                    V $"%.1f{inputMb} MB"
                    V $"%.1f{outputMb} MB"
                    MC(color, EncodeResult.savingsDisplay r)
                ]
            )

        let struct (totalInput, totalOutput) =
            (struct (0L, 0L), results)
            ||> List.fold (fun (struct (ti, to')) r -> struct (ti + r.InputSize, to' + r.OutputSize))

        let allRows =
            if totalInput > 0L then
                let totalInputMb = float totalInput / float Constants.BytesPerMB

                let totalOutputMb =
                    float totalOutput / float Constants.BytesPerMB

                let totalPct =
                    float (totalOutput - totalInput) / float totalInput * 100.0

                let sign = if totalPct > 0.0 then "+" else ""

                let color =
                    if totalPct < 0.0 then
                        Color.Green
                    else
                        Color.Red

                let totalsRow =
                    Payloads [
                        P "Total"
                        V ""
                        P $"%.1f{totalInputMb} MB"
                        P $"%.1f{totalOutputMb} MB"
                        MC(color, $"%s{sign}%.1f{totalPct}%%")
                    ]

                dataRows @ [ totalsRow ]
            else
                dataRows

        table columns allRows
        |> withTitle "Encoding Results"
        |> toOutputPayload
        |> toConsole

    let displayVerification (results: PipelineResult list) : bool =
        P "Verifying outputs..." |> toConsole

        let outcomes =
            results
            |> List.map (fun r ->
                let fileName = OutputPath.fileName r.Encode.OutputPath

                match r.Verification with
                | Ok() ->
                    Many [
                        MC(Color.Green, "\u2713")
                        V $" %s{fileName}"
                    ]
                    |> toConsole

                    true
                | Error issues ->
                    Many [
                        MC(Color.Red, "\u2717")
                        V $" %s{fileName}"
                    ]
                    |> toConsole

                    for issue in issues do
                        Many [
                            MC(Color.Red, "  \u2192")
                            V $" %s{VerificationIssue.format issue}"
                        ]
                        |> toConsole

                    false
            )

        List.forall id outcomes

    /// Corrected SpectreCoff.Progress.startCustom — upstream ignores AutoClear/HideCompleted.
    let private startProgress (template: ProgressTemplate) (operation: ProgressOperation<'T>) =
        AnsiConsole
            .Progress()
            .AutoClear(template.AutoClear)
            .AutoRefresh(template.AutoRefresh)
            .HideCompleted(template.HideCompleted)
            .Columns(template.Columns |> List.toArray)
            .StartAsync(operation)

    let private processWithContext
        (infos: MediaFileInfo list)
        (runOne: MediaFileInfo -> ProgressReporter -> CancellationToken -> Task<Result<PipelineResult, AppError>>)
        (ct: CancellationToken)
        (ctx: ProgressContext)
        : Task<Result<PipelineResult list, AppError>>
        =
        let total = infos.Length

        let rec loop (remaining: MediaFileInfo list) (acc: PipelineResult list) (idx: int) =
            task {
                match remaining with
                | [] -> return Ok(List.rev acc)
                | info :: rest ->
                    let description =
                        $"[%d{idx}/%d{total}] %s{MediaFilePath.name info.Path}"

                    let progressTask =
                        realizeIn ctx (HotCustomTask(info.DurationSecs, description))

                    let onProgress elapsed = progressTask.Value <- elapsed

                    match! runOne info onProgress ct with
                    | Ok pipelineResult ->
                        progressTask.Value <- info.DurationSecs
                        return! loop rest (pipelineResult :: acc) (idx + 1)
                    | Error e -> return Error e
            }

        loop infos [] 1

    let withProgress
        (infos: MediaFileInfo list)
        (runOne: MediaFileInfo -> ProgressReporter -> CancellationToken -> Task<Result<PipelineResult, AppError>>)
        (ct: CancellationToken)
        : Task<Result<PipelineResult list, AppError>>
        =

        let template = {
            AutoClear = false
            AutoRefresh = true
            HideCompleted = false
            Columns = [
                SpinnerColumn()
                TaskDescriptionColumn()
                ProgressBarColumn()
                PercentageColumn()
                ElapsedTimeColumn()
                RemainingTimeColumn()
            ]
        }

        startProgress template (processWithContext infos runOne ct)
