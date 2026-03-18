namespace WebOptimise

open System

/// Pure EBML binary parser for Matroska/WebM container verification.
[<RequireQualifiedAccess>]
module Ebml =

    let cuesElementId =
        ReadOnlyMemory(
            [|
                0x1Cuy
                0x53uy
                0xBBuy
                0x6Buy
            |]
        )

    let clusterElementId =
        ReadOnlyMemory(
            [|
                0x1Fuy
                0x43uy
                0xB6uy
                0x75uy
            |]
        )

    let vintWidth (firstByte: byte) : int =
        if firstByte = 0uy then
            0
        else
            9
            - (int (System.Numerics.BitOperations.Log2(uint firstByte)) + 1)

    let readElementId (data: ReadOnlySpan<byte>) (pos: int) : struct (ReadOnlyMemory<byte> * int) voption =
        if pos >= data.Length then
            ValueNone
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                ValueNone
            else
                let slice = data.Slice(pos, width).ToArray()
                ValueSome(struct (ReadOnlyMemory slice, pos + width))

    let readElementSize (data: ReadOnlySpan<byte>) (pos: int) : struct (int * int) voption =
        if pos >= data.Length then
            ValueNone
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                ValueNone
            else
                let mutable value = 0L

                for i in pos .. pos + width - 1 do
                    value <- (value <<< 8) ||| int64 data[i]

                let mask = (1L <<< (8 * width)) - 1L
                let masked = value &&& (mask >>> width)
                ValueSome(struct (int masked, pos + width))

    let spanEqual (a: ReadOnlyMemory<byte>) (b: ReadOnlyMemory<byte>) = a.Span.SequenceEqual(b.Span)

    let skipEbmlHeader (data: ReadOnlySpan<byte>) : Result<int, string> =
        match readElementId data 0 with
        | ValueNone -> Error "Could not parse EBML header"
        | ValueSome(struct (_, pos1)) ->

            match readElementSize data pos1 with
            | ValueNone -> Error "Could not parse EBML header"
            | ValueSome(struct (size, pos2)) -> Ok(pos2 + size)

    let enterSegment (data: ReadOnlySpan<byte>) (pos: int) : Result<int, string> =
        match readElementId data pos with
        | ValueNone -> Error "Could not parse Segment element"
        | ValueSome(struct (_, segEnd)) ->

            match readElementSize data segEnd with
            | ValueNone -> Error "Could not parse Segment element"
            | ValueSome(struct (_, dataStart)) -> Ok dataStart

    [<TailCall>]
    let rec scanForCues (data: ReadOnlySpan<byte>) (pos: int) : Result<unit, string> =
        if pos >= data.Length then
            Error "Could not locate Cues or Cluster element in file header"
        else

            match readElementId data pos with
            | ValueNone -> Error "Could not locate Cues or Cluster element in file header"
            | ValueSome(struct (eId, idEnd)) ->

                match readElementSize data idEnd with
                | ValueNone -> Error "Could not locate Cues or Cluster element in file header"
                | ValueSome(struct (eSize, eDataStart)) ->

                    if spanEqual eId cuesElementId then
                        Ok()
                    elif spanEqual eId clusterElementId then
                        Error "Cues not at front: first Cluster appears before Cues element"
                    else
                        scanForCues data (eDataStart + eSize)

    /// Check that EBML Cues element appears before the first Cluster element.
    /// Returns Ok () if Cues is front-loaded, Error with a message otherwise.
    let checkCuesBeforeCluster (data: ReadOnlySpan<byte>) : Result<unit, string> =
        if data.Length < 4 then
            Error "File too small for EBML verification"
        else

            match skipEbmlHeader data with
            | Error e -> Error e
            | Ok segPos ->

                match enterSegment data segPos with
                | Error e -> Error e
                | Ok dataStart -> scanForCues data dataStart
