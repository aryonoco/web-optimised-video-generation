namespace WebOptimise

open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<NoComparison; NoEquality>]
type Env = {
    RunBuffered: string -> string list -> Task<Result<BufferedOutput, ShellError>>
    RunStreaming:
        string -> string list -> (string -> unit) -> StringBuilder -> CancellationToken -> Task<Result<int, ShellError>>
    RunExists: string -> Task<Result<unit, ShellError>>
    ResolveInputPath: string -> ResolvedPath
    EnumerateFiles: OutputDir -> string list
    CreateDirectory: OutputDir -> Result<unit, ShellError>
    FileLength: OutputPath -> Result<int64, ShellError>
    FileExists: OutputPath -> bool
    DeleteFile: OutputPath -> Result<unit, ShellError>
    ReadFileHeader: OutputPath -> int -> Result<byte array, ShellError>
    ParseJson: string -> Result<JsonElement, string>
}

[<RequireQualifiedAccess>]
module Env =

    val live: Env
