namespace WebOptimise

open System.Text.Json

[<RequireQualifiedAccess>]
module ProbeParse =

    val fromJson: path: MediaFilePath -> root: JsonElement -> Result<MediaFileInfo, AppError>
