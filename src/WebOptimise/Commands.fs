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

/// Pure ffmpeg command builders. No I/O.
[<RequireQualifiedAccess>]
module Commands =

    let render (cmd: FfmpegCmd) : string list = [
        "-hide_banner"
        "-y"
        "-i"
        MediaFilePath.value cmd.Input
        yield!
            match cmd.Video with
            | VideoAction.Copy -> [
                "-c:v"
                "copy"
              ]
            | VideoAction.EncodeX264 s ->
                [
                    "-c:v"
                    "libx264"
                    "-preset"
                    s.Preset
                    "-crf"
                    s.Crf.ToString()
                    "-profile:v"
                    s.Profile
                    "-level:v"
                    s.Level
                    "-pix_fmt"
                    "yuv420p"
                    "-g"
                    s.GopSize.ToString()
                    "-keyint_min"
                    s.MinKeyint.ToString()
                    "-bf"
                    s.BFrames.ToString()
                    "-x264-params"
                    s.X264Params
                ]
        "-c:a"
        "copy"
        "-map"
        "0:v:0"
        "-map"
        "0:a:0"
        yield!
            match cmd.Container with
            | Container.Mp4Faststart -> [
                "-movflags"
                "+faststart"
              ]
            | Container.WebmDash clusterTimeLimit ->
                [
                    "-f"
                    "webm"
                    "-cues_to_front"
                    "true"
                    "-cluster_time_limit"
                    clusterTimeLimit.ToString()
                    "-write_crc32"
                    "false"
                ]
        "-map_metadata"
        "-1"
        if cmd.StripChapters then
            "-map_chapters"
            "-1"
        "-progress"
        "pipe:1"
        OutputPath.value cmd.Output
    ]

    let buildEncode (info: MediaFileInfo) (output: OutputPath) : FfmpegCmd =
        let fps = max (int (round info.Video.FrameRate)) 1
        let gopSize = fps * Constants.KeyframeIntervalSecs

        {
            Input = info.Path
            Output = output
            Video =
                VideoAction.EncodeX264 {
                    Preset = Constants.Preset
                    Crf = Constants.Crf
                    Profile = Constants.Profile
                    Level = Constants.Level
                    GopSize = gopSize
                    MinKeyint = fps
                    BFrames = Constants.BFrames
                    X264Params = Constants.X264Params
                }
            Audio = AudioAction.Copy
            Container = Container.Mp4Faststart
            StripMetadata = true
            StripChapters = false
        }

    let buildRemux (info: MediaFileInfo) (output: OutputPath) : FfmpegCmd = {
        Input = info.Path
        Output = output
        Video = VideoAction.Copy
        Audio = AudioAction.Copy
        Container = Container.Mp4Faststart
        StripMetadata = true
        StripChapters = true
    }

    let buildWebmRemux (info: MediaFileInfo) (output: OutputPath) : FfmpegCmd = {
        Input = info.Path
        Output = output
        Video = VideoAction.Copy
        Audio = AudioAction.Copy
        Container = Container.WebmDash 2000
        StripMetadata = true
        StripChapters = true
    }
