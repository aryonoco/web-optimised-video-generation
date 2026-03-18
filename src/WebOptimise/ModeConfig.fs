namespace WebOptimise

open System.Threading.Tasks

type CmdBuilder = MediaFileInfo -> string -> string list

type Verifier = string -> Task<Result<unit, string list>>

[<NoComparison; NoEquality>]
type ModeConfig = {
    CmdBuilder: CmdBuilder
    Verifier: Verifier
    OutputExt: OutputExtension
    ErrorVerb: string
    Label: string
    CompletionVerb: string
}

[<RequireQualifiedAccess>]
module ModeConfig =

    let private remuxConfig = {
        CmdBuilder = Commands.buildRemuxCmd
        Verifier = Verify.verifyRemuxed
        OutputExt = OutputExtension.mp4
        ErrorVerb = "Remuxing"
        Label = "remux"
        CompletionVerb = "optimised"
    }

    let private encodeConfig = {
        CmdBuilder = Commands.buildEncodeCmd
        Verifier = Verify.verifyEncoded
        OutputExt = OutputExtension.mp4
        ErrorVerb = "Encoding"
        Label = "encode"
        CompletionVerb = "encoded"
    }

    let private webmConfig = {
        CmdBuilder = Commands.buildWebmRemuxCmd
        Verifier = Verify.verifyWebm
        OutputExt = OutputExtension.webm
        ErrorVerb = "WebM remuxing"
        Label = "webm"
        CompletionVerb = "optimised"
    }

    let forMode (mode: Mode) =
        match mode with
        | Mode.Remux -> remuxConfig
        | Mode.Encode -> encodeConfig
        | Mode.Webm -> webmConfig

    let label (mode: Mode) = (forMode mode).Label

    let completionVerb (mode: Mode) = (forMode mode).CompletionVerb
