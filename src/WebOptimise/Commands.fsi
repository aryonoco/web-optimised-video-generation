namespace WebOptimise

[<RequireQualifiedAccess>]
module Commands =

    val buildEncodeCmd: info: MediaFileInfo -> outputPath: string -> string list

    val buildRemuxCmd: info: MediaFileInfo -> outputPath: string -> string list

    val buildWebmRemuxCmd: info: MediaFileInfo -> outputPath: string -> string list
