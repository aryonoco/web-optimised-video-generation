namespace WebOptimise

open System

/// Pure EBML binary parser for Matroska/WebM container verification.
module Ebml =

    let private cuesElementId = ReadOnlyMemory([| 0x1Cuy; 0x53uy; 0xBBuy; 0x6Buy |])
    let private clusterElementId = ReadOnlyMemory([| 0x1Fuy; 0x43uy; 0xB6uy; 0x75uy |])

    let private vintWidth (firstByte: byte) : int =
        if firstByte = 0uy then 0
        else 9 - (int (System.Numerics.BitOperations.Log2 (uint firstByte)) + 1)

    let private readElementId (data: ReadOnlySpan<byte>) (pos: int) : struct (ReadOnlyMemory<byte> * int) =
        if pos >= data.Length then
            struct (ReadOnlyMemory.Empty, pos)
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                struct (ReadOnlyMemory.Empty, pos)
            else
                let slice = data.Slice(pos, width).ToArray()
                struct (ReadOnlyMemory slice, pos + width)

    let private readElementSize (data: ReadOnlySpan<byte>) (pos: int) : struct (int * int) =
        if pos >= data.Length then
            struct (-1, pos)
        else
            let width = vintWidth data[pos]

            if width = 0 || pos + width > data.Length then
                struct (-1, pos)
            else
                let mutable value = 0L

                for i in pos .. pos + width - 1 do
                    value <- (value <<< 8) ||| int64 data[i]

                let mask = (1L <<< (8 * width)) - 1L
                let masked = value &&& (mask >>> width)
                struct (int masked, pos + width)

    let private spanEqual (a: ReadOnlyMemory<byte>) (b: ReadOnlyMemory<byte>) =
        a.Span.SequenceEqual(b.Span)

    /// Check that EBML Cues element appears before the first Cluster element.
    /// Returns Ok () if Cues is front-loaded, Error with a message otherwise.
    let checkCuesBeforeCluster (data: ReadOnlySpan<byte>) : Result<unit, string> =
        if data.Length < 4 then
            Error "File too small for EBML verification"
        else
            // Skip EBML header element
            let struct (elemId, pos1) = readElementId data 0
            let struct (size, pos2) = readElementSize data pos1

            if elemId.IsEmpty || size < 0 then
                Error "Could not parse EBML header"
            else
                let mutable pos = pos2 + size

                // Read Segment element header
                let struct (segId, segEnd) = readElementId data pos
                let struct (_, dataStart) = readElementSize data segEnd

                if segId.IsEmpty then
                    Error "Could not parse Segment element"
                else
                    pos <- dataStart
                    let mutable result = Error "Could not locate Cues or Cluster element in file header"
                    let mutable found = false

                    while pos < data.Length && not found do
                        let struct (eId, idEnd) = readElementId data pos
                        let struct (eSize, eDataStart) = readElementSize data idEnd

                        if eId.IsEmpty || eSize < 0 then
                            found <- true
                        elif spanEqual eId cuesElementId then
                            result <- Ok()
                            found <- true
                        elif spanEqual eId clusterElementId then
                            result <- Error "Cues not at front: first Cluster appears before Cues element"
                            found <- true
                        else
                            pos <- eDataStart + eSize

                    result
