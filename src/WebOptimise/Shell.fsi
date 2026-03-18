namespace WebOptimise

open System.Text
open System.Threading
open System.Threading.Tasks

[<Struct; NoComparison; NoEquality>]
type BufferedOutput = {
    ExitCode: int
    StdOut: string
    StdErr: string
}

[<RequireQualifiedAccess>]
module Shell =

    val runBuffered: tool: string -> args: string list -> Task<Result<BufferedOutput, ShellError>>

    val runStreaming:
        tool: string ->
        args: string list ->
        onStdOutLine: (string -> unit) ->
        stdErrBuilder: StringBuilder ->
        ct: CancellationToken ->
            Task<Result<int, ShellError>>

    val runExists: tool: string -> Task<Result<unit, ShellError>>

    val resolveInputPath: path: string -> ResolvedPath

    val enumerateFiles: dir: string -> string list

    val createDirectory: dir: string -> Result<unit, ShellError>
    val fileLength: path: string -> Result<int64, ShellError>
    val fileExists: path: string -> bool
    val deleteFile: path: string -> Result<unit, ShellError>
    val readFileHeader: path: string -> maxBytes: int -> Result<byte array, ShellError>
