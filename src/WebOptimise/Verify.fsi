namespace WebOptimise

open System.Threading.Tasks

[<RequireQualifiedAccess>]
module Verify =

    val verifyEncoded: path: string -> Task<Result<unit, string list>>

    val verifyRemuxed: path: string -> Task<Result<unit, string list>>

    val verifyWebm: path: string -> Task<Result<unit, string list>>
