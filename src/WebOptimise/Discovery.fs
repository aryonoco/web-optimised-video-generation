namespace WebOptimise

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions

/// File discovery, filename utilities, and mode resolution. Pure — no I/O.
[<RequireQualifiedAccess>]
module Discovery =

    let naturalComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering)

    let isSupported (ext: string) =
        Constants.supportedExtensions.Contains ext

    let isMkv (ext: string) = Constants.mkvExtensions.Contains ext

    let slugify (name: string) =
        let stem =
            Path.GetFileNameWithoutExtension name |> NullSafe.path

        let slug =
            Regex.Replace(stem.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-')

        if String.IsNullOrEmpty slug then
            "unnamed"
        else
            slug

    let sanitiseFilename (guid: Guid) (name: string) (ext: OutputExtension) : string =
        $"%O{guid}_%s{slugify name}%s{OutputExtension.value ext}"

    let matchExistingOutput (files: string list) (originalName: string) (ext: OutputExtension) : string option =
        let suffix =
            $"_%s{slugify originalName}%s{OutputExtension.value ext}"

        files
        |> List.tryFind (fun f -> (Path.GetFileName(f) |> NullSafe.path).EndsWith(suffix, StringComparison.Ordinal))

    let effectiveMode (info: MediaFileInfo) (userMode: Mode) : Mode =
        if isMkv (MediaFilePath.extension info.Path) then
            Mode.Webm
        else
            userMode

    let outputDir (info: MediaFileInfo) : string =
        Path.Combine(MediaFilePath.directory info.Path, Constants.OutputDirName)

    let collectFiles (resolved: ResolvedPath list) : Result<MediaFilePath list, AppError> =
        let addIfNew (found, seen) full =
            if Set.contains full seen then
                (found, seen)
            else
                (full :: found, Set.add full seen)

        let processPath (found, seen) =
            function
            | ResolvedPath.File(fullPath, ext) ->
                if not (isSupported ext) then
                    Error(AppError.ValidationError $"Unsupported file type '%s{ext}': %s{fullPath}")
                else
                    Ok(addIfNew (found, seen) fullPath)
            | ResolvedPath.Directory(_, files) ->
                let filtered =
                    files
                    |> List.filter (fun f ->
                        isSupported (Path.GetExtension f |> NullSafe.path)
                        && (Path.GetDirectoryName f
                            |> NullSafe.path
                            |> Path.GetFileName
                            |> NullSafe.path)
                           <> Constants.OutputDirName
                    )
                    |> List.sortWith (fun a b ->
                        naturalComparer.Compare(
                            Path.GetFileName a |> NullSafe.path,
                            Path.GetFileName b |> NullSafe.path
                        )
                    )

                Ok(List.fold (fun (f, s) file -> addIfNew (f, s) file) (found, seen) filtered)
            | ResolvedPath.NotFound p -> Error(AppError.ValidationError $"Path does not exist: %s{p}")

        resolved
        |> List.fold (fun acc rp -> acc |> Result.bind (fun state -> processPath state rp)) (Ok([], Set.empty))
        |> Result.map (fun (found, _) -> found |> List.rev |> List.map MediaFilePath.ofTrusted)

    let rejectMkvEncode (files: MediaFilePath list) (mode: Mode) : Result<unit, AppError> =
        match mode with
        | Mode.Encode ->
            let mkvFiles =
                files
                |> List.filter (fun f -> isMkv (MediaFilePath.extension f))

            if mkvFiles.IsEmpty then
                Ok()
            else
                let names =
                    mkvFiles
                    |> List.map MediaFilePath.name
                    |> String.concat ", "

                Error(AppError.ValidationError $"--mode encode is not supported for MKV files: %s{names}")
        | Mode.Remux
        | Mode.Webm -> Ok()

    let validateMkvCodecs (infos: MediaFileInfo list) : Result<unit, AppError> =
        let errors =
            infos
            |> List.collect (fun info ->
                if not (isMkv (MediaFilePath.extension info.Path)) then
                    []
                else
                    [
                        if info.Video.Codec <> VideoCodec.AV1 then
                            $"%s{MediaFilePath.name info.Path}: video codec is %s{VideoCodec.displayName info.Video.Codec} (requires AV1 for WebM)"
                        match info.Audio with
                        | ValueNone ->
                            $"%s{MediaFilePath.name info.Path}: no audio stream found (requires Opus for WebM)"
                        | ValueSome audio when audio.Codec <> AudioCodec.Opus ->
                            $"%s{MediaFilePath.name info.Path}: audio codec is %s{AudioCodec.displayName audio.Codec} (requires Opus for WebM)"
                        | ValueSome _ -> ()
                    ]
            )

        if errors.IsEmpty then
            Ok()
        else
            let msg =
                "MKV codec requirements not met:\n"
                + (errors
                   |> List.map (fun e -> $"  %s{e}")
                   |> String.concat "\n")

            Error(AppError.ValidationError msg)
