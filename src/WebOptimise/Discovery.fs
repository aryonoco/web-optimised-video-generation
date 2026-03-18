namespace WebOptimise

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling

/// File discovery, filename utilities, and mode resolution. Pure — no I/O.
[<RequireQualifiedAccess>]
module Discovery =

    let naturalComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering)

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

    let matchExistingOutput (files: string list) (originalName: string) (ext: OutputExtension) : OutputPath voption =
        let suffix =
            $"_%s{slugify originalName}%s{OutputExtension.value ext}"

        files
        |> List.tryFind (fun f -> (Path.GetFileName(f) |> NullSafe.path).EndsWith(suffix, StringComparison.Ordinal))
        |> Option.bind (fun f ->
            match OutputPath.ofFullPath f with
            | Ok op -> Some op
            | Error _ -> None
        )
        |> ValueOption.ofOption

    let effectiveMode (info: MediaFileInfo) (userMode: Mode) : Mode =
        if ContainerFormat.isMkv (MediaFilePath.container info.Path) then
            Mode.Webm
        else
            userMode

    let outputDir (info: MediaFileInfo) : OutputDir = OutputDir.forFile info.Path

    let collectFiles (resolved: ResolvedPath list) : Result<NonEmpty<MediaFilePath>, AppError> =
        let addIfNew (found, seen) full =
            if Set.contains full seen then
                (found, seen)
            else
                (full :: found, Set.add full seen)

        let processPath (found, seen) =
            function
            | ResolvedPath.File(fullPath, _) -> Ok(addIfNew (found, seen) fullPath)
            | ResolvedPath.UnsupportedFile(fullPath, ext) ->
                Error(AppError.Validation(ValidationFailure.UnsupportedExtension(ext, fullPath)))
            | ResolvedPath.Directory(_, files) ->
                let filtered =
                    files
                    |> List.filter (fun f ->
                        ContainerFormat.ofExtension (Path.GetExtension f |> NullSafe.path)
                        |> ValueOption.isSome
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
            | ResolvedPath.NotFound p -> Error(AppError.Validation(ValidationFailure.PathNotFound p))

        resolved
        |> List.fold (fun acc rp -> acc |> Result.bind (fun state -> processPath state rp)) (Ok([], Set.empty))
        |> Result.bind (fun (found, _) ->
            found
            |> List.rev
            |> List.traverseResultM (fun p ->
                MediaFilePath.create p
                |> Result.mapError (fun reason -> AppError.Validation(ValidationFailure.InvalidPath reason))
            )
        )
        |> Result.bind (fun files ->
            match NonEmpty.ofList files with
            | ValueSome nel -> Ok nel
            | ValueNone -> Error(AppError.Validation ValidationFailure.NoSupportedFiles)
        )

    let rejectMkvEncode (files: MediaFilePath list) (mode: Mode) : Result<unit, AppError> =
        match mode with
        | Mode.Encode ->
            let mkvFiles =
                files
                |> List.filter (fun f -> ContainerFormat.isMkv (MediaFilePath.container f))

            if mkvFiles.IsEmpty then
                Ok()
            else
                Error(
                    AppError.Validation(
                        ValidationFailure.MkvEncodeNotSupported(mkvFiles |> List.map MediaFilePath.name)
                    )
                )
        | Mode.Remux
        | Mode.Webm -> Ok()

    let validateMkvCodecs (infos: MediaFileInfo list) : Result<unit, AppError> =
        let violations =
            infos
            |> List.collect (fun info ->
                if not (ContainerFormat.isMkv (MediaFilePath.container info.Path)) then
                    []
                else
                    let name = MediaFilePath.name info.Path

                    [
                        if info.Video.Codec <> VideoCodec.AV1 then
                            MkvCodecViolation.WrongVideoCodec(name, info.Video.Codec)
                        match info.Audio with
                        | ValueNone -> MkvCodecViolation.MissingAudio name
                        | ValueSome audio when audio.Codec <> AudioCodec.Opus ->
                            MkvCodecViolation.WrongAudioCodec(name, audio.Codec)
                        | ValueSome _ -> ()
                    ]
            )

        if violations.IsEmpty then
            Ok()
        else
            Error(AppError.Validation(ValidationFailure.MkvCodecViolations violations))
