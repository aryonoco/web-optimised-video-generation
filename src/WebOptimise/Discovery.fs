namespace WebOptimise

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling

/// File discovery, filename utilities, and mode resolution.
[<RequireQualifiedAccess>]
module Discovery =

    let private nonNullPath (s: string | null) =
        match s with
        | null -> ""
        | v -> v

    let private naturalComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering)

    let private isSupported (ext: string) =
        Constants.supportedExtensions.Contains ext

    let private isMkv (ext: string) = Constants.mkvExtensions.Contains ext

    let private slugify (name: string) =
        let stem = Path.GetFileNameWithoutExtension name |> nonNullPath
        let slug = Regex.Replace(stem.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-')
        if String.IsNullOrEmpty slug then "unnamed" else slug

    let sanitiseFilename (name: string) (ext: OutputExtension) : string =
        $"%O{Guid.NewGuid()}_%s{slugify name}%s{OutputExtension.value ext}"

    let findExistingOutput (outputDir: string) (originalName: string) (ext: OutputExtension) : string option =
        let suffix = $"_%s{slugify originalName}%s{OutputExtension.value ext}"

        if Directory.Exists outputDir then
            Directory.EnumerateFiles outputDir
            |> Seq.tryFind (fun f -> (Path.GetFileName(f) |> nonNullPath).EndsWith(suffix, StringComparison.Ordinal))
        else
            None

    let effectiveMode (info: MediaFileInfo) (userMode: Mode) : Mode =
        if isMkv (MediaFilePath.extension info.Path) then
            Mode.Webm
        else
            userMode

    let outputDir (info: MediaFileInfo) : string =
        Path.Combine(MediaFilePath.directory info.Path, Constants.OutputDirName)

    let findFiles (paths: string list) : Result<MediaFilePath list, AppError> =
        result {
            let mutable found: string list = []
            let mutable seen = Set.empty<string>

            for p in paths do
                let resolved = Path.GetFullPath p

                if File.Exists resolved then
                    let ext = Path.GetExtension resolved |> nonNullPath

                    if not (isSupported ext) then
                        return! Error(AppError.ValidationError $"Unsupported file type '%s{ext}': %s{p}")

                    if not (seen.Contains resolved) then
                        seen <- seen.Add resolved
                        found <- found @ [ resolved ]

                elif Directory.Exists resolved then
                    let files =
                        Directory.EnumerateFiles resolved
                        |> Seq.filter (fun f ->
                            isSupported (Path.GetExtension f |> nonNullPath)
                            && (Path.GetDirectoryName f |> nonNullPath |> Path.GetFileName |> nonNullPath)
                               <> Constants.OutputDirName)
                        |> Seq.sortWith (fun a b ->
                            naturalComparer.Compare(
                                Path.GetFileName a |> nonNullPath,
                                Path.GetFileName b |> nonNullPath
                            ))

                    for f in files do
                        let full = Path.GetFullPath f

                        if not (seen.Contains full) then
                            seen <- seen.Add full
                            found <- found @ [ full ]
                else
                    return! Error(AppError.ValidationError $"Path does not exist: %s{p}")

            return found |> List.map MediaFilePath.ofTrusted
        }

    let rejectMkvEncode (files: MediaFilePath list) (mode: Mode) : Result<unit, AppError> =
        match mode with
        | Mode.Encode ->
            let mkvFiles = files |> List.filter (fun f -> isMkv (MediaFilePath.extension f))

            if mkvFiles.IsEmpty then
                Ok()
            else
                let names = mkvFiles |> List.map MediaFilePath.name |> String.concat ", "
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
                    [ if info.Video.Codec <> "av1" then
                          $"%s{MediaFilePath.name info.Path}: video codec is %s{info.Video.Codec} (requires AV1 for WebM)"
                      match info.Audio with
                      | ValueNone ->
                          $"%s{MediaFilePath.name info.Path}: no audio stream found (requires Opus for WebM)"
                      | ValueSome audio when audio.Codec <> "opus" ->
                          $"%s{MediaFilePath.name info.Path}: audio codec is %s{audio.Codec} (requires Opus for WebM)"
                      | ValueSome _ -> () ])

        if errors.IsEmpty then
            Ok()
        else
            let msg =
                "MKV codec requirements not met:\n"
                + (errors |> List.map (fun e -> $"  %s{e}") |> String.concat "\n")

            Error(AppError.ValidationError msg)
