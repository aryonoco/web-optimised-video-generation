namespace WebOptimise

[<RequireQualifiedAccess>]
module ProbeParse =

    val fromJson: path: MediaFilePath -> json: string -> Result<MediaFileInfo, AppError>
