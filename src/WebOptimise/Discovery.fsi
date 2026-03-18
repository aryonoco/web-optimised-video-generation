namespace WebOptimise

open System

[<RequireQualifiedAccess>]
module Discovery =

    val slugify: name: string -> string

    val sanitiseFilename: guid: Guid -> name: string -> ext: OutputExtension -> string

    val matchExistingOutput: files: string list -> originalName: string -> ext: OutputExtension -> OutputPath voption

    val effectiveMode: info: MediaFileInfo -> userMode: Mode -> Mode

    val outputDir: info: MediaFileInfo -> OutputDir

    val collectFiles: resolved: ResolvedPath list -> Result<NonEmpty<MediaFilePath>, AppError>

    val rejectMkvEncode: files: MediaFilePath list -> mode: Mode -> Result<unit, AppError>

    val validateMkvCodecs: infos: MediaFileInfo list -> Result<unit, AppError>
