namespace WebOptimise

/// Pure ffmpeg command builders. No I/O.
[<RequireQualifiedAccess>]
module Commands =

    let buildEncodeCmd (info: MediaFileInfo) (outputPath: string) : string list =
        let fps = max (int (round info.Video.FrameRate)) 1
        let gopSize = fps * Constants.KeyframeIntervalSecs

        [ "-hide_banner"
          "-y"
          "-i"
          MediaFilePath.value info.Path
          "-c:v"
          "libx264"
          "-preset"
          Constants.Preset
          "-crf"
          Constants.Crf.ToString()
          "-profile:v"
          Constants.Profile
          "-level:v"
          Constants.Level
          "-pix_fmt"
          "yuv420p"
          "-g"
          gopSize.ToString()
          "-keyint_min"
          fps.ToString()
          "-bf"
          Constants.BFrames.ToString()
          "-x264-params"
          Constants.X264Params
          "-c:a"
          "copy"
          "-map"
          "0:v:0"
          "-map"
          "0:a:0"
          "-movflags"
          "+faststart"
          "-map_metadata"
          "-1"
          "-progress"
          "pipe:1"
          outputPath ]

    let buildRemuxCmd (info: MediaFileInfo) (outputPath: string) : string list =
        [ "-hide_banner"
          "-y"
          "-i"
          MediaFilePath.value info.Path
          "-c:v"
          "copy"
          "-c:a"
          "copy"
          "-map"
          "0:v:0"
          "-map"
          "0:a:0"
          "-movflags"
          "+faststart"
          "-map_metadata"
          "-1"
          "-map_chapters"
          "-1"
          "-progress"
          "pipe:1"
          outputPath ]

    let buildWebmRemuxCmd (info: MediaFileInfo) (outputPath: string) : string list =
        [ "-hide_banner"
          "-y"
          "-i"
          MediaFilePath.value info.Path
          "-c:v"
          "copy"
          "-c:a"
          "copy"
          "-map"
          "0:v:0"
          "-map"
          "0:a:0"
          "-f"
          "webm"
          "-cues_to_front"
          "true"
          "-cluster_time_limit"
          "2000"
          "-write_crc32"
          "false"
          "-map_metadata"
          "-1"
          "-map_chapters"
          "-1"
          "-progress"
          "pipe:1"
          outputPath ]
