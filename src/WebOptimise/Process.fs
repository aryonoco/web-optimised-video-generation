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
                    Path.Combine(outputDir, Discovery.sanitiseFilename (MediaFilePath.name info.Path) config.OutputExt)

                let args = config.CmdBuilder info outputPath
                let stdErrBuilder = StringBuilder()

                let! shellResult =
                    Shell.runStreaming "ffmpeg" args (parseProgressLine onProgress info.DurationSecs) stdErrBuilder ct

                match shellResult with
                | Error "cancelled" ->
                    if File.Exists outputPath then
                        File.Delete outputPath

                    return Error(AppError.IoError "Operation cancelled")
                | Error msg ->
                    if File.Exists outputPath then
                        File.Delete outputPath

                    return Error(AppError.EncodeError($"Unexpected error: %s{msg}", ValueNone))
                | Ok exitCode when exitCode <> 0 ->
                    if File.Exists outputPath then
                        File.Delete outputPath

                    let stderr = stdErrBuilder.ToString()

                    return
                        Error(
                            AppError.EncodeError(
                                $"%s{config.ErrorVerb} failed for %s{MediaFilePath.name info.Path}: exit code %d{exitCode}\n%s{stderr}",
                                ValueSome("ffmpeg" :: args)
                            )
                        )
                | Ok _ ->

                    let outputSize = System.IO.FileInfo(outputPath).Length

                    return
                        Ok {
                            InputPath = info.Path
                            OutputPath = outputPath
                            InputSize = info.SizeBytes
                            OutputSize = outputSize
                        }
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

            let existing =
                Discovery.findExistingOutput outputDir (MediaFilePath.name info.Path) config.OutputExt

            match existing, overwrite with
            | Some existingPath, false ->
                onProgress info.DurationSecs
                let outputSize = System.IO.FileInfo(existingPath).Length

                return
                    Ok {
                        InputPath = info.Path
                        OutputPath = existingPath
                        InputSize = info.SizeBytes
                        OutputSize = outputSize
                    }
            | Some existingPath, true ->
                File.Delete existingPath
                return! runEncode info outputDir config onProgress ct
            | None, _ -> return! runEncode info outputDir config onProgress ct
        }
