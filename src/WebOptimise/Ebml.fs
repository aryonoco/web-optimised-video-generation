namespace WebOptimise

open System

/// Pure EBML binary parser for Matroska/WebM container verification.
[<RequireQualifiedAccess>]
module Ebml =

    let private cuesElementId: byte array = [|
        0x1Cuy
        0x53uy
        0xBBuy
        0x6Buy
    |]

    let private clusterElementId: byte array = [|
        0x1Fuy
        0x43uy
        0xB6uy
        0x75uy
    |]

    let vintWidth (firstByte: byte) : int =
        if firstByte = 0uy then
            0
        else
            9
            - (int (System.Numerics.BitOperations.Log2(uint firstByte)) + 1)

    let private skipVint (data: ReadOnlySpan<byte>) (pos: int) : int voption =
        if pos >= data.Length then
            ValueNone
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                ValueNone
            else
                ValueSome(pos + width)

    let private matchesId (data: ReadOnlySpan<byte>) (pos: int) (width: int) (target: byte array) : bool =
        width = target.Length
        && data.Slice(pos, width).SequenceEqual(ReadOnlySpan(target))

    [<TailCall>]
    let rec private foldVintBytes (data: ReadOnlySpan<byte>) (i: int) (endPos: int) (acc: int64) : int64 =
        if i >= endPos then
            acc
        else
            foldVintBytes data (i + 1) endPos ((acc <<< 8) ||| int64 data[i])

    let readElementSize (data: ReadOnlySpan<byte>) (pos: int) : struct (int64 * int) voption =
        if pos >= data.Length then
            ValueNone
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                ValueNone
            else
                let endPos = pos + width
                let value = foldVintBytes data pos endPos 0L
                let mask = (1L <<< (7 * width)) - 1L
                ValueSome(struct (value &&& mask, endPos))

    let skipEbmlHeader (data: ReadOnlySpan<byte>) : Result<int, string> =
        match skipVint data 0 with
        | ValueNone -> Error "Could not parse EBML header"
        | ValueSome pos1 ->

            match readElementSize data pos1 with
            | ValueNone -> Error "Could not parse EBML header"
            | ValueSome(struct (size, pos2)) -> Ok(pos2 + int size)

    let enterSegment (data: ReadOnlySpan<byte>) (pos: int) : Result<int, string> =
        match skipVint data pos with
        | ValueNone -> Error "Could not parse Segment element"
        | ValueSome segEnd ->

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
                let idWidth = vintWidth data[pos]

                if idWidth = 0 || pos + idWidth > data.Length then
                    ScanError "Could not locate Cues or Cluster element in file header"
                else
                    let idEnd = pos + idWidth

                    match readElementSize data idEnd with
                    | ValueNone -> ScanError "Could not parse element size in file header"
                    | ValueSome(struct (eSize, eDataStart)) ->

                        if matchesId data pos idWidth cuesElementId then
                            CuesFound
                        elif matchesId data pos idWidth clusterElementId then
                            ScanError "Cues not at front: first Cluster appears before Cues element"
                        else
                            let nextPos = int64 eDataStart + eSize

                            if nextPos > int64 data.Length then
                                ScanError "Element extends beyond scanned header region"
                            else
                                Scanning(int nextPos)

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
