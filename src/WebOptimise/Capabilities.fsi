namespace WebOptimise

open System.Text
open System.Threading
open System.Threading.Tasks

[<NoComparison; NoEquality>]
type Env = {
    RunBuffered: string -> string list -> Task<Result<BufferedOutput, ShellError>>
    RunStreaming:
        string -> string list -> (string -> unit) -> StringBuilder -> CancellationToken -> Task<Result<int, ShellError>>
    RunExists: string -> Task<Result<unit, ShellError>>
    ResolveInputPath: string -> ResolvedPath
    EnumerateFiles: string -> string list
    CreateDirectory: string -> Result<unit, ShellError>
    FileLength: string -> Result<int64, ShellError>
    FileExists: string -> bool
    DeleteFile: string -> Result<unit, ShellError>
    ReadFileHeader: string -> int -> Result<byte array, ShellError>
}

[<RequireQualifiedAccess>]
module Env =

    val live: Env
