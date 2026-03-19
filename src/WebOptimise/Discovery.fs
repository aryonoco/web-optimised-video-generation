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

    let private caseInsensitiveFs =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()

    let private normalizeForDedup (path: string) =
        if caseInsensitiveFs then
            path.ToUpperInvariant()
        else
            path

    let slugify (name: string) =
        let stem =
            Path.GetFileNameWithoutExtension name |> Unchecked.nonNull

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
        |> List.tryFind (fun f -> (Path.GetFileName f |> Unchecked.nonNull).EndsWith(suffix, StringComparison.Ordinal))
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
            let key = normalizeForDedup full

            if Set.contains key seen then
                (found, seen)
            else
                (full :: found, Set.add key seen)

        let processPath ((found, seen), errors) =
            function
            | ResolvedPath.File(fullPath, _) -> addIfNew (found, seen) fullPath, errors
            | ResolvedPath.UnsupportedFile(fullPath, ext) ->
                (found, seen),
                AppError.Validation(ValidationFailure.UnsupportedExtension(ext, fullPath))
                :: errors
            | ResolvedPath.Directory(_, files) ->
                let filtered =
                    files
                    |> List.filter (fun f ->
                        ContainerFormat.ofExtension (Path.GetExtension f |> Unchecked.nonNull)
                        |> ValueOption.isSome
                        && (match Path.GetDirectoryName f with
                            | null -> ""
                            | d -> Path.GetFileName d |> Unchecked.nonNull)
                           <> Constants.OutputDirName
                    )
                    |> List.sortWith (fun a b ->
                        naturalComparer.Compare(
                            Path.GetFileName a |> Unchecked.nonNull,
                            Path.GetFileName b |> Unchecked.nonNull
                        )
                    )

                List.fold (fun (f, s) file -> addIfNew (f, s) file) (found, seen) filtered, errors
            | ResolvedPath.NotFound p ->
                (found, seen),
                AppError.Validation(ValidationFailure.PathNotFound p)
                :: errors

        let (found, _), errors =
            resolved |> List.fold processPath (([], Set.empty), [])

        match NonEmpty.ofList (List.rev errors) with
        | ValueSome(NonEmpty(single, [])) -> Error single
        | ValueSome nel -> Error(AppError.Multiple nel)
        | ValueNone ->
            found
            |> List.rev
            |> List.traverseResultM (fun p ->
                MediaFilePath.create p
                |> Result.mapError (fun reason -> AppError.Validation(ValidationFailure.InvalidPath reason))
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

    let remuxWarnings (infos: MediaFileInfo list) : string list =
        infos
        |> List.collect (fun info ->
            if ContainerFormat.isMkv (MediaFilePath.container info.Path) then
                []
            else
                let name = MediaFilePath.name info.Path

                [
                    if info.Video.Codec <> VideoCodec.H264 then
                        $"%s{name}: codec is %s{VideoCodec.displayName info.Video.Codec} (not H.264). Use --mode encode to re-encode."
                    elif info.Video.Profile <> ValueSome VideoProfile.High then
                        let profileLabel =
                            match info.Video.Profile with
                            | ValueSome p -> VideoProfile.displayName p
                            | ValueNone -> "N/A"

                        $"%s{name}: profile is %s{profileLabel} (not High). Use --mode encode to upgrade."
                ]
        )
