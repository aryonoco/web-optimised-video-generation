namespace WebOptimise

open System.Threading.Tasks

[<RequireQualifiedAccess>]
module Verify =

    val verifyEncoded: path: OutputPath -> Task<Result<unit, string list>>

    val verifyRemuxed: path: OutputPath -> Task<Result<unit, string list>>

    val verifyWebm: path: OutputPath -> Task<Result<unit, string list>>
