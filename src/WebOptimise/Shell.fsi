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

    val runBuffered: tool: string -> args: string list -> Task<Result<BufferedOutput, string>>

    val runStreaming:
        tool: string ->
        args: string list ->
        onStdOutLine: (string -> unit) ->
        stdErrBuilder: StringBuilder ->
        ct: CancellationToken ->
            Task<Result<int, string>>

    val runExists: tool: string -> Task<Result<unit, string>>
