namespace WebOptimise

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open FsToolkit.ErrorHandling

/// ffmpeg execution with progress tracking.
[<RequireQualifiedAccess>]
module Process =

    let private parseProgressLine (onProgress: float -> unit) (totalDuration: float) (line: string) =
        if line.StartsWith("out_time_us=", StringComparison.Ordinal) then
            let valueStr = line.AsSpan().Slice(12) // "out_time_us=" is 12 chars

            match Int64.TryParse valueStr with
            | true, usec ->
                let elapsed = float usec / float Constants.MicrosecondsPerSecond
                onProgress elapsed
            | _ -> ()
        elif line = "progress=end" then
            onProgress totalDuration

    let private runEncode
        (info: MediaFileInfo)
        (outputDir: string)
        (config: ModeConfig)
        (onProgress: float -> unit)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>> =
        taskResult {
            let createDirResult =
                try
                    Directory.CreateDirectory outputDir |> ignore
                    Ok()
                with ex ->
                    Error(AppError.IoError $"Cannot create output directory: %s{ex.Message}")

            do! createDirResult |> Task.FromResult

            let outputPath =
                Path.Combine(outputDir, Discovery.sanitiseFilename (MediaFilePath.name info.Path) config.OutputExt)

            let args = config.CmdBuilder info outputPath
            let stdErrBuilder = StringBuilder()

            let cmd =
                Cli
                    .Wrap("ffmpeg")
                    .WithArguments(args)
                    .WithValidation(CommandResultValidation.None)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(parseProgressLine onProgress info.DurationSecs))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuilder))

            let! execResult =
                task {
                    try
                        let! r = cmd.ExecuteAsync(ct)
                        return Ok r
                    with
                    | :? OperationCanceledException ->
                        if File.Exists outputPath then
                            File.Delete outputPath

                        return Error(AppError.IoError "Operation cancelled")
                    | ex ->
                        if File.Exists outputPath then
                            File.Delete outputPath

                        return Error(AppError.EncodeError($"Unexpected error: %s{ex.Message}", ValueNone))
                }

            if execResult.ExitCode <> 0 then
                if File.Exists outputPath then
                    File.Delete outputPath

                let stderr = stdErrBuilder.ToString()

                return!
                    Error(
                        AppError.EncodeError(
                            $"%s{config.ErrorVerb} failed for %s{MediaFilePath.name info.Path}: exit code %d{execResult.ExitCode}\n%s{stderr}",
                            ValueSome("ffmpeg" :: args)
                        )
                    )
            else

                let outputSize = System.IO.FileInfo(outputPath).Length

                return
                    { InputPath = info.Path
                      OutputPath = outputPath
                      InputSize = info.SizeBytes
                      OutputSize = outputSize }
        }

    let processFile
        (info: MediaFileInfo)
        (outputDir: string)
        (mode: Mode)
        (overwrite: bool)
        (onProgress: float -> unit)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>> =
        task {
            let config = ModeConfig.forMode mode

            let existing =
                Discovery.findExistingOutput outputDir (MediaFilePath.name info.Path) config.OutputExt

            match existing, overwrite with
            | Some existingPath, false ->
                onProgress info.DurationSecs
                let outputSize = System.IO.FileInfo(existingPath).Length

                return
                    Ok
                        { InputPath = info.Path
                          OutputPath = existingPath
                          InputSize = info.SizeBytes
                          OutputSize = outputSize }
            | Some existingPath, true ->
                File.Delete existingPath
                return! runEncode info outputDir config onProgress ct
            | None, _ -> return! runEncode info outputDir config onProgress ct
        }
