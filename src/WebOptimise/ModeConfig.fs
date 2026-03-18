namespace WebOptimise

open System.Threading.Tasks

type CmdBuilder = MediaFileInfo -> OutputPath -> FfmpegCmd

type Verifier = Env -> OutputPath -> Task<Result<unit, VerificationIssue list>>

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
        CmdBuilder = Commands.buildRemux
        Verifier = Verify.checkFaststart
        OutputExt = OutputExtension.mp4
        ErrorVerb = "Remuxing"
        Label = "remux"
        CompletionVerb = "optimised"
    }

    let private encodeConfig = {
        CmdBuilder = Commands.buildEncode
        Verifier = Verify.verifyEncoded
        OutputExt = OutputExtension.mp4
        ErrorVerb = "Encoding"
        Label = "encode"
        CompletionVerb = "encoded"
    }

    let private webmConfig = {
        CmdBuilder = Commands.buildWebmRemux
        Verifier = Verify.checkCuesFront
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
