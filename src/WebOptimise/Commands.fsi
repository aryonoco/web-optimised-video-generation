namespace WebOptimise

[<Struct; RequireQualifiedAccess>]
type VideoAction =
    | Copy
    | Encode of
        preset: string *
        crf: int *
        profile: string *
        level: string *
        gopSize: int *
        minKeyint: int *
        bframes: int *
        x264Params: string

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
