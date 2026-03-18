namespace WebOptimise

open System.Text
open System.Text.Json
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

    val enumerateFiles: dir: OutputDir -> string list

    val createDirectory: dir: OutputDir -> Result<unit, ShellError>
    val fileLength: path: OutputPath -> Result<int64, ShellError>
    val fileExists: path: OutputPath -> bool
    val deleteFile: path: OutputPath -> Result<unit, ShellError>
    val readFileHeader: path: OutputPath -> maxBytes: int -> Result<byte array, ShellError>
    val parseJson: json: string -> Result<JsonElement, string>
