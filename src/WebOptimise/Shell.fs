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

    let runBuffered (tool: string) (args: string list) : Task<Result<BufferedOutput, ShellError>> =
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
            | :? ComponentModel.Win32Exception -> return Error(ShellError.NotFound tool)
            | ex -> return Error(ShellError.Failed(tool, ex.Message))
        }

    let runStreaming
        (tool: string)
        (args: string list)
        (onStdOutLine: string -> unit)
        (stdErrBuilder: StringBuilder)
        (ct: CancellationToken)
        : Task<Result<int, ShellError>>
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
            | :? OperationCanceledException -> return Error ShellError.Cancelled
            | :? ComponentModel.Win32Exception -> return Error(ShellError.NotFound tool)
            | ex -> return Error(ShellError.Failed(tool, ex.Message))
        }

    let runExists (tool: string) : Task<Result<unit, ShellError>> =
        task {
            match! runBuffered tool [ "-version" ] with
            | Ok _ -> return Ok()
            | Error msg -> return Error msg
        }

    let resolveInputPath (path: string) : ResolvedPath =
        let resolved = System.IO.Path.GetFullPath path

        if System.IO.File.Exists resolved then
            ResolvedPath.File(resolved, System.IO.Path.GetExtension resolved |> NullSafe.path)
        elif System.IO.Directory.Exists resolved then
            let files =
                System.IO.Directory.EnumerateFiles resolved |> Seq.toList

            ResolvedPath.Directory(resolved, files)
        else
            ResolvedPath.NotFound path

    let enumerateFiles (dir: string) : string list =
        if System.IO.Directory.Exists dir then
            System.IO.Directory.EnumerateFiles dir |> Seq.toList
        else
            []
