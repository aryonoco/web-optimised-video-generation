namespace WebOptimise

open System.IO
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
                    C(Discovery.sanitiseFilename (MediaFilePath.name info.Path) config.OutputExt)
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
                    V(Path.GetFileName r.OutputPath |> NullSafe.path)
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

    let private verifyOne (pr: ProcessResult) : Task<bool> =
        task {
            let config = ModeConfig.forMode pr.Mode
            let! verifyResult = config.Verifier pr.Result.OutputPath

            let name =
                Path.GetFileName pr.Result.OutputPath |> NullSafe.path

            match verifyResult with
            | Ok() ->
                Many [
                    MC(Color.Green, "\u2713")
                    V $" %s{name}"
                ]
                |> toConsole

                return true
            | Error issues ->
                Many [
                    MC(Color.Red, "\u2717")
                    V $" %s{name}"
                ]
                |> toConsole

                for issue in issues do
                    Many [
                        MC(Color.Red, "  \u2192")
                        V $" %s{issue}"
                    ]
                    |> toConsole

                return false
        }

    let displayVerification (results: ProcessResult list) : Task<bool> =
        task {
            P "Verifying outputs..." |> toConsole

            let! outcomes = results |> List.map verifyOne |> Task.WhenAll

            return outcomes |> Array.forall id
        }

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
        (processOne:
            MediaFileInfo -> Mode -> (float -> unit) -> CancellationToken -> Task<Result<EncodeResult, AppError>>)
        (userMode: Mode)
        (ct: CancellationToken)
        (ctx: ProgressContext)
        : Task<Result<ProcessResult list, AppError>>
        =
        let total = infos.Length

        let rec loop (remaining: MediaFileInfo list) (acc: ProcessResult list) (idx: int) =
            task {
                match remaining with
                | [] -> return Ok(List.rev acc)
                | info :: rest ->
                    let fileMode = Discovery.effectiveMode info userMode

                    let description =
                        $"[%d{idx}/%d{total}] %s{MediaFilePath.name info.Path}"

                    let progressTask =
                        realizeIn ctx (HotCustomTask(info.DurationSecs, description))

                    let onProgress elapsed = progressTask.Value <- elapsed

                    match! processOne info fileMode onProgress ct with
                    | Ok encodeResult ->
                        progressTask.Value <- info.DurationSecs

                        return!
                            loop
                                rest
                                ({
                                    Result = encodeResult
                                    Mode = fileMode
                                 }
                                 :: acc)
                                (idx + 1)
                    | Error e -> return Error e
            }

        loop infos [] 1

    let withProgress
        (infos: MediaFileInfo list)
        (processOne:
            MediaFileInfo -> Mode -> (float -> unit) -> CancellationToken -> Task<Result<EncodeResult, AppError>>)
        (userMode: Mode)
        (ct: CancellationToken)
        : Task<Result<ProcessResult list, AppError>>
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

        startProgress template (processWithContext infos processOne userMode ct)
