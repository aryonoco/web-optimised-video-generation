namespace WebOptimise

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling

/// ffmpeg execution with progress tracking.
[<RequireQualifiedAccess>]
module Process =

    let private parseProgressLine (onProgress: ProgressReporter) (totalDuration: float) (line: string) =
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
        (env: Env)
        (info: MediaFileInfo)
        (outputDir: string)
        (config: ModeConfig)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        task {
            match env.CreateDirectory outputDir with
            | Error e -> return Error(AppError.Process(ProcessFailure.OutputDirFailed(outputDir, ShellError.format e)))
            | Ok() ->

                match
                    OutputPath.create
                        outputDir
                        (Discovery.sanitiseFilename (Guid.NewGuid()) (MediaFilePath.name info.Path) config.OutputExt)
                with
                | Error msg -> return Error(AppError.Process(ProcessFailure.OutputDirFailed(outputDir, msg)))
                | Ok outputPath ->

                    let cmd = config.CmdBuilder info outputPath
                    let args = Commands.render cmd
                    let stdErrBuilder = StringBuilder()

                    let! shellResult =
                        env.RunStreaming "ffmpeg" args (parseProgressLine onProgress info.DurationSecs) stdErrBuilder ct

                    let encodeResult =
                        match shellResult with
                        | Error ShellError.Cancelled -> Error(AppError.Process ProcessFailure.Cancelled)
                        | Error e -> Error(AppError.Process(ProcessFailure.ShellFailed e))
                        | Ok exitCode when exitCode <> 0 ->
                            let stderr = stdErrBuilder.ToString()

                            Error(
                                AppError.Process(
                                    ProcessFailure.FfmpegFailed(
                                        exitCode,
                                        stderr,
                                        "ffmpeg" :: args,
                                        MediaFilePath.name info.Path,
                                        config.ErrorVerb
                                    )
                                )
                            )
                        | Ok _ ->
                            match env.FileLength(OutputPath.value outputPath) with
                            | Error e -> Error(AppError.Process(ProcessFailure.ShellFailed e))
                            | Ok outputSize ->
                                Ok {
                                    InputPath = info.Path
                                    OutputPath = outputPath
                                    InputSize = info.SizeBytes
                                    OutputSize = outputSize
                                }

                    match encodeResult with
                    | Error e ->
                        if env.FileExists(OutputPath.value outputPath) then
                            env.DeleteFile(OutputPath.value outputPath) |> ignore

                        return Error e
                    | Ok r -> return Ok r
        }

    let processFile
        (env: Env)
        (info: MediaFileInfo)
        (outputDir: string)
        (mode: Mode)
        (overwrite: bool)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        task {
            let config = ModeConfig.forMode mode

            let existingFiles = env.EnumerateFiles outputDir

            let existing =
                Discovery.matchExistingOutput existingFiles (MediaFilePath.name info.Path) config.OutputExt

            match existing, overwrite with
            | ValueSome existingFullPath, false ->
                onProgress info.DurationSecs

                match env.FileLength existingFullPath with
                | Error e -> return Error(AppError.Process(ProcessFailure.ShellFailed e))
                | Ok outputSize ->
                    match OutputPath.ofFullPath existingFullPath with
                    | Error msg -> return Error(AppError.Process(ProcessFailure.OutputDirFailed(existingFullPath, msg)))
                    | Ok existingPath ->
                        return
                            Ok {
                                InputPath = info.Path
                                OutputPath = existingPath
                                InputSize = info.SizeBytes
                                OutputSize = outputSize
                            }
            | ValueSome existingFullPath, true ->
                match env.DeleteFile existingFullPath with
                | Error e -> return Error(AppError.Process(ProcessFailure.ShellFailed e))
                | Ok() -> return! runEncode env info outputDir config onProgress ct
            | ValueNone, _ -> return! runEncode env info outputDir config onProgress ct
        }
