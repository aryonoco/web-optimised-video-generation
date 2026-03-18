namespace WebOptimise

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
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
        : Task<Result<EncodeResult, AppError>>
        =
        task {
            let createDirResult =
                try
                    Directory.CreateDirectory outputDir |> ignore
                    Ok()
                with ex ->
                    Error(AppError.IoError $"Cannot create output directory: %s{ex.Message}")

            match createDirResult with
            | Error e -> return Error e
            | Ok() ->

                let outputPath =
                    OutputPath.create
                        outputDir
                        (Discovery.sanitiseFilename (Guid.NewGuid()) (MediaFilePath.name info.Path) config.OutputExt)

                let cmd = config.CmdBuilder info outputPath
                let args = Commands.render cmd
                let stdErrBuilder = StringBuilder()

                let! shellResult =
                    Shell.runStreaming "ffmpeg" args (parseProgressLine onProgress info.DurationSecs) stdErrBuilder ct

                let encodeResult =
                    match shellResult with
                    | Error ShellError.Cancelled -> Error(AppError.IoError "Operation cancelled")
                    | Error e -> Error(AppError.ofShellError e)
                    | Ok exitCode when exitCode <> 0 ->
                        let stderr = stdErrBuilder.ToString()

                        Error(
                            AppError.EncodeError(
                                $"%s{config.ErrorVerb} failed for %s{MediaFilePath.name info.Path}: exit code %d{exitCode}\n%s{stderr}",
                                ValueSome("ffmpeg" :: args)
                            )
                        )
                    | Ok _ ->
                        let outputSize =
                            System.IO.FileInfo(OutputPath.value outputPath).Length

                        Ok {
                            InputPath = info.Path
                            OutputPath = outputPath
                            InputSize = info.SizeBytes
                            OutputSize = outputSize
                        }

                match encodeResult with
                | Error e ->
                    if File.Exists(OutputPath.value outputPath) then
                        File.Delete(OutputPath.value outputPath)

                    return Error e
                | Ok r -> return Ok r
        }

    let processFile
        (info: MediaFileInfo)
        (outputDir: string)
        (mode: Mode)
        (overwrite: bool)
        (onProgress: float -> unit)
        (ct: CancellationToken)
        : Task<Result<EncodeResult, AppError>>
        =
        task {
            let config = ModeConfig.forMode mode

            let existingFiles = Shell.enumerateFiles outputDir

            let existing =
                Discovery.matchExistingOutput existingFiles (MediaFilePath.name info.Path) config.OutputExt
                |> Option.map OutputPath.ofFullPath

            match existing, overwrite with
            | Some existingPath, false ->
                onProgress info.DurationSecs

                let outputSize =
                    System.IO.FileInfo(OutputPath.value existingPath).Length

                return
                    Ok {
                        InputPath = info.Path
                        OutputPath = existingPath
                        InputSize = info.SizeBytes
                        OutputSize = outputSize
                    }
            | Some existingPath, true ->
                File.Delete(OutputPath.value existingPath)
                return! runEncode info outputDir config onProgress ct
            | None, _ -> return! runEncode info outputDir config onProgress ct
        }
