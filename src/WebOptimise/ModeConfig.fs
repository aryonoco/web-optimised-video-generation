namespace WebOptimise

open System.Collections.Frozen
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

    let private configs: FrozenDictionary<Mode, ModeConfig> =
        (dict [
            Mode.Remux,
            {
                CmdBuilder = Commands.buildRemuxCmd
                Verifier = Verify.verifyRemuxed
                OutputExt = OutputExtension.mp4
                ErrorVerb = "Remuxing"
                Label = "remux"
                CompletionVerb = "optimised"
            }

            Mode.Encode,
            {
                CmdBuilder = Commands.buildEncodeCmd
                Verifier = Verify.verifyEncoded
                OutputExt = OutputExtension.mp4
                ErrorVerb = "Encoding"
                Label = "encode"
                CompletionVerb = "encoded"
            }

            Mode.Webm,
            {
                CmdBuilder = Commands.buildWebmRemuxCmd
                Verifier = Verify.verifyWebm
                OutputExt = OutputExtension.webm
                ErrorVerb = "WebM remuxing"
                Label = "webm"
                CompletionVerb = "optimised"
            }
        ])
            .ToFrozenDictionary()

    let forMode (mode: Mode) = configs[mode]

    let label (mode: Mode) = configs[mode].Label

    let completionVerb (mode: Mode) = configs[mode].CompletionVerb
