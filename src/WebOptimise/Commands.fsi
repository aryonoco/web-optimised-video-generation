namespace WebOptimise

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type VideoAction =
    | Copy
    | EncodeX264 of X264Settings

[<Struct; RequireQualifiedAccess>]
type AudioAction = | Copy

[<Struct; RequireQualifiedAccess>]
type Container =
    | Mp4Faststart
    | WebmDash of clusterTimeLimit: int

[<NoComparison; NoEquality>]
type FfmpegCmd = {
    Input: MediaFilePath
    Output: OutputPath
    Video: VideoAction
    Audio: AudioAction
    Container: Container
    StripMetadata: bool
    StripChapters: bool
}

[<RequireQualifiedAccess>]
module Commands =

    val render: cmd: FfmpegCmd -> string list

    val buildEncode: info: MediaFileInfo -> output: OutputPath -> FfmpegCmd

    val buildRemux: info: MediaFileInfo -> output: OutputPath -> FfmpegCmd

    val buildWebmRemux: info: MediaFileInfo -> output: OutputPath -> FfmpegCmd
