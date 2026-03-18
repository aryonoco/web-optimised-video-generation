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

    let private interpretExitCode
        (info: MediaFileInfo)
        (errorVerb: string)
        (args: string list)
        (stdErrBuilder: StringBuilder)
        (shellResult: Result<int, ShellError>)
        : Result<unit, AppError>
        =
        match shellResult with
        | Error ShellError.Cancelled -> Error(AppError.Process ProcessFailure.Cancelled)
        | Error e -> Error(AppError.Process(ProcessFailure.ShellFailed e))
        | Ok exitCode when exitCode <> 0 ->
            Error(
                AppError.Process(
                    ProcessFailure.FfmpegFailed(
                        exitCode,
                        stdErrBuilder.ToString(),
                        "ffmpeg" :: args,
                        MediaFilePath.name info.Path,
                        errorVerb
                    )
                )
            )
        | Ok _ -> Ok()

    let private tryCleanup (env: Env) (path: OutputPath) : unit =
        if env.FileExists path then
            env.DeleteFile path |> ignore

    let private runEncode
        (env: Env)
        (info: MediaFileInfo)
        (outputDir: OutputDir)
        (config: ModeConfig)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        let stagingDir = StagingDir.forOutput outputDir

        let setupResult =
            result {
                do!
                    env.CreateDirectory outputDir
                    |> Result.mapError (fun e ->
                        AppError.Process(
                            ProcessFailure.OutputDirFailed(OutputDir.value outputDir, ShellError.format e)
                        )
                    )

                do!
                    env.CreateStagingDir stagingDir
                    |> Result.mapError (fun e ->
                        AppError.Process(
                            ProcessFailure.OutputDirFailed(StagingDir.value stagingDir, ShellError.format e)
                        )
                    )

                let baseName =
                    Discovery.sanitiseFilename (Guid.NewGuid()) (MediaFilePath.name info.Path) config.OutputExt

                let! finalPath =
                    OutputPath.create (OutputDir.value outputDir) baseName
                    |> Result.mapError (fun msg ->
                        AppError.Process(ProcessFailure.OutputDirFailed(OutputDir.value outputDir, msg))
                    )

                let! stagingPath =
                    OutputPath.create (StagingDir.value stagingDir) baseName
                    |> Result.mapError (fun msg ->
                        AppError.Process(ProcessFailure.OutputDirFailed(StagingDir.value stagingDir, msg))
                    )

                return struct (finalPath, stagingPath)
            }

        match setupResult with
        | Error e -> Task.FromResult(Error e)
        | Ok(struct (finalPath, stagingPath)) ->

            task {
                let cmd = config.CmdBuilder info stagingPath
                let args = Commands.render cmd
                let stdErrBuilder = StringBuilder()

                let! shellResult =
                    env.RunStreaming "ffmpeg" args (parseProgressLine onProgress info.DurationSecs) stdErrBuilder ct

                let encodeResult =
                    result {
                        do! interpretExitCode info config.ErrorVerb args stdErrBuilder shellResult

                        do!
                            env.MoveFile stagingPath finalPath
                            |> Result.mapError (fun e -> AppError.Process(ProcessFailure.ShellFailed e))

                        let! outputSize =
                            env.FileLength finalPath
                            |> Result.mapError (fun e -> AppError.Process(ProcessFailure.ShellFailed e))

                        return {
                            InputPath = info.Path
                            OutputPath = finalPath
                            InputSize = info.SizeBytes
                            OutputSize = outputSize
                        }
                    }

                match encodeResult with
                | Ok r -> return Ok r
                | Error e ->
                    tryCleanup env stagingPath
                    return Error e
            }

    let processFile
        (env: Env)
        (info: MediaFileInfo)
        (outputDir: OutputDir)
        (mode: Mode)
        (overwrite: bool)
        (onProgress: ProgressReporter)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        let config = ModeConfig.forMode mode
        let existingFiles = env.EnumerateFiles outputDir

        let existing =
            Discovery.matchExistingOutput existingFiles (MediaFilePath.name info.Path) config.OutputExt

        match existing, overwrite with
        | ValueSome existingPath, false ->
            onProgress info.DurationSecs

            let skipResult =
                result {
                    let! outputSize =
                        env.FileLength existingPath
                        |> Result.mapError (fun e -> AppError.Process(ProcessFailure.ShellFailed e))

                    return {
                        InputPath = info.Path
                        OutputPath = existingPath
                        InputSize = info.SizeBytes
                        OutputSize = outputSize
                    }
                }

            Task.FromResult skipResult
        | ValueSome existingPath, true ->
            task {
                match env.DeleteFile existingPath with
                | Error e -> return Error(AppError.Process(ProcessFailure.ShellFailed e))
                | Ok() -> return! runEncode env info outputDir config onProgress ct
            }
        | ValueNone, _ -> runEncode env info outputDir config onProgress ct
