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
                let bytes = data.Slice(pos, width).ToArray()

                let value =
                    (0L, bytes)
                    ||> Array.fold (fun acc b -> (acc <<< 8) ||| int64 b)

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

    [<Struct>]
    type private ScanState =
        | Scanning of scanPos: int
        | CuesFound
        | ScanError of scanMsg: string

    let private stepScan (data: ReadOnlySpan<byte>) (state: ScanState) : ScanState =
        match state with
        | CuesFound
        | ScanError _ -> state
        | Scanning pos ->
            if pos >= data.Length then
                ScanError "Could not locate Cues or Cluster element in file header"
            else

                match readElementId data pos with
                | ValueNone -> ScanError "Could not locate Cues or Cluster element in file header"
                | ValueSome(struct (eId, idEnd)) ->

                    match readElementSize data idEnd with
                    | ValueNone -> ScanError "Could not parse element size in file header"
                    | ValueSome(struct (eSize, eDataStart)) ->

                        if spanEqual eId cuesElementId then
                            CuesFound
                        elif spanEqual eId clusterElementId then
                            ScanError "Cues not at front: first Cluster appears before Cues element"
                        else
                            Scanning(eDataStart + eSize)

    [<TailCall>]
    let rec private scanLoop (data: ReadOnlySpan<byte>) (state: ScanState) : Result<unit, string> =
        match state with
        | CuesFound -> Ok()
        | ScanError msg -> Error msg
        | Scanning _ -> scanLoop data (stepScan data state)

    let private runScan (data: ReadOnlySpan<byte>) (startPos: int) : Result<unit, string> =
        scanLoop data (Scanning startPos)

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
                | Ok dataStart -> runScan data dataStart
