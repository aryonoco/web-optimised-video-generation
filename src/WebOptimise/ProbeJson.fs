namespace WebOptimise

open System.Text.Json.Serialization

/// Mutable DTOs for System.Text.Json deserialization of ffprobe JSON output.
/// These exist only at the parsing boundary and are immediately converted to domain types.

[<CLIMutable; NoComparison; NoEquality>]
type RawStream =
    { [<JsonPropertyName("codec_type")>]
      CodecType: string

      [<JsonPropertyName("codec_name")>]
      CodecName: string

      [<JsonPropertyName("profile")>]
      Profile: string

      [<JsonPropertyName("width")>]
      Width: int

      [<JsonPropertyName("height")>]
      Height: int

      [<JsonPropertyName("r_frame_rate")>]
      RFrameRate: string

      [<JsonPropertyName("channels")>]
      Channels: int

      [<JsonPropertyName("sample_rate")>]
      SampleRate: string

      [<JsonPropertyName("bit_rate")>]
      BitRate: string }

[<CLIMutable; NoComparison; NoEquality>]
type RawFormat =
    { [<JsonPropertyName("duration")>]
      Duration: string

      [<JsonPropertyName("size")>]
      Size: string }

[<CLIMutable; NoComparison; NoEquality>]
type RawProbeData =
    { [<JsonPropertyName("streams")>]
      Streams: RawStream array

      [<JsonPropertyName("format")>]
      Format: RawFormat }
