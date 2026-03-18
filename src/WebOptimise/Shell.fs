namespace WebOptimise

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open System.Text.Json
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
            let ext = System.IO.Path.GetExtension resolved |> NullSafe.path

            match ContainerFormat.ofExtension ext with
            | ValueSome container -> ResolvedPath.File(resolved, container)
            | ValueNone -> ResolvedPath.UnsupportedFile(resolved, ext)
        elif System.IO.Directory.Exists resolved then
            let files =
                System.IO.Directory.EnumerateFiles resolved |> Seq.toList

            ResolvedPath.Directory(resolved, files)
        else
            ResolvedPath.NotFound path

    let enumerateFiles (dir: OutputDir) : string list =
        let d = OutputDir.value dir

        if System.IO.Directory.Exists d then
            System.IO.Directory.EnumerateFiles d |> Seq.toList
        else
            []

    let createDirectory (dir: OutputDir) : Result<unit, ShellError> =
        try
            System.IO.Directory.CreateDirectory(OutputDir.value dir)
            |> ignore

            Ok()
        with ex ->
            Error(ShellError.Failed("filesystem", ex.Message))

    let fileLength (path: OutputPath) : Result<int64, ShellError> =
        try
            Ok(System.IO.FileInfo(OutputPath.value path).Length)
        with ex ->
            Error(ShellError.Failed("filesystem", ex.Message))

    let fileExists (path: OutputPath) : bool =
        System.IO.File.Exists(OutputPath.value path)

    let deleteFile (path: OutputPath) : Result<unit, ShellError> =
        try
            System.IO.File.Delete(OutputPath.value path)
            Ok()
        with ex ->
            Error(ShellError.Failed("filesystem", ex.Message))

    let readFileHeader (path: OutputPath) (maxBytes: int) : Result<byte array, ShellError> =
        try
            use fs = System.IO.File.OpenRead(OutputPath.value path)
            let buf = Array.zeroCreate (min (int fs.Length) maxBytes)
            let bytesRead = fs.Read(buf, 0, buf.Length)

            if bytesRead = buf.Length then
                Ok buf
            else
                Ok(buf[.. bytesRead - 1])
        with ex ->
            Error(ShellError.Failed("filesystem", ex.Message))

    let parseJson (json: string) : Result<JsonElement, string> =
        try
            Ok(JsonElement.Parse(json))
        with ex ->
            Error ex.Message
