namespace WebOptimise

[<RequireQualifiedAccess>]
module Discovery =

    val sanitiseFilename: name: string -> ext: OutputExtension -> string

    val findExistingOutput: outputDir: string -> originalName: string -> ext: OutputExtension -> string option

    val effectiveMode: info: MediaFileInfo -> userMode: Mode -> Mode

    val outputDir: info: MediaFileInfo -> string

    val findFiles: paths: string list -> Result<MediaFilePath list, AppError>

    val rejectMkvEncode: files: MediaFilePath list -> mode: Mode -> Result<unit, AppError>

    val validateMkvCodecs: infos: MediaFileInfo list -> Result<unit, AppError>
