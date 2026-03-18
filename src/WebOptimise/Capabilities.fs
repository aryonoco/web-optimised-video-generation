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
}

[<RequireQualifiedAccess>]
module Env =

    let live: Env = {
        RunBuffered = Shell.runBuffered
        RunStreaming = Shell.runStreaming
        RunExists = Shell.runExists
        ResolveInputPath = Shell.resolveInputPath
        EnumerateFiles = Shell.enumerateFiles
    }
