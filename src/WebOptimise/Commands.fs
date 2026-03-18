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
            | VideoAction.Encode(preset, crf, profile, level, gopSize, minKeyint, bframes, x264Params) ->
                [
                    "-c:v"
                    "libx264"
                    "-preset"
                    preset
                    "-crf"
                    crf.ToString()
                    "-profile:v"
                    profile
                    "-level:v"
                    level
                    "-pix_fmt"
                    "yuv420p"
                    "-g"
                    gopSize.ToString()
                    "-keyint_min"
                    minKeyint.ToString()
                    "-bf"
                    bframes.ToString()
                    "-x264-params"
                    x264Params
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
                VideoAction.Encode(
                    Constants.Preset,
                    Constants.Crf,
                    Constants.Profile,
                    Constants.Level,
                    gopSize,
                    fps,
                    Constants.BFrames,
                    Constants.X264Params
                )
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
