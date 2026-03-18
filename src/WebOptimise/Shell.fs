namespace WebOptimise

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open CliWrap.Buffered

[<Struct; NoComparison; NoEquality>]
type BufferedOutput = {
    ExitCode: int
    StdOut: string
    StdErr: string
}

[<RequireQualifiedAccess>]
module Shell =

    let runBuffered (tool: string) (args: string list) : Task<Result<BufferedOutput, string>> =
        task {
            try
                let! r =
                    Cli
                        .Wrap(tool)
                        .WithArguments(args)
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteBufferedAsync()

                return
                    Ok {
                        ExitCode = r.ExitCode
                        StdOut = r.StandardOutput
                        StdErr = r.StandardError
                    }
            with
            | :? ComponentModel.Win32Exception -> return Error $"%s{tool} not found in PATH"
            | ex -> return Error $"%s{tool} failed: %s{ex.Message}"
        }

    let runStreaming
        (tool: string)
        (args: string list)
        (onStdOutLine: string -> unit)
        (stdErrBuilder: StringBuilder)
        (ct: CancellationToken)
        : Task<Result<int, string>>
        =
        task {
            try
                let! r =
                    Cli
                        .Wrap(tool)
                        .WithArguments(args)
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToDelegate(onStdOutLine))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuilder))
                        .ExecuteAsync(ct)

                return Ok r.ExitCode
            with
            | :? OperationCanceledException -> return Error "cancelled"
            | :? ComponentModel.Win32Exception -> return Error $"%s{tool} not found in PATH"
            | ex -> return Error $"Unexpected error: %s{ex.Message}"
        }

    let runExists (tool: string) : Task<Result<unit, string>> =
        task {
            match! runBuffered tool [ "-version" ] with
            | Ok _ -> return Ok()
            | Error msg -> return Error msg
        }
