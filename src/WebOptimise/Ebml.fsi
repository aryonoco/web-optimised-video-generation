namespace WebOptimise

open System

[<RequireQualifiedAccess>]
module Ebml =

    val checkCuesBeforeCluster: data: ReadOnlySpan<byte> -> Result<unit, string>
