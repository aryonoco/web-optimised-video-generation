namespace WebOptimise

open System.Collections.Frozen

type CmdBuilder = MediaFileInfo -> string -> string list

type Verifier = string -> Result<unit, string list>

type ModeConfig =
    { CmdBuilder: CmdBuilder
      Verifier: Verifier
      OutputExt: OutputExtension
      ErrorVerb: string
      Label: string
      CompletionVerb: string }

module ModeConfig =

    let private configs: FrozenDictionary<Mode, ModeConfig> =
        (dict
            [ Remux,
              { CmdBuilder = Commands.buildRemuxCmd
                Verifier = Verify.verifyRemuxed
                OutputExt = OutputExtension.mp4
                ErrorVerb = "Remuxing"
                Label = "remux"
                CompletionVerb = "optimised" }

              Encode,
              { CmdBuilder = Commands.buildEncodeCmd
                Verifier = Verify.verifyEncoded
                OutputExt = OutputExtension.mp4
                ErrorVerb = "Encoding"
                Label = "encode"
                CompletionVerb = "encoded" }

              Webm,
              { CmdBuilder = Commands.buildWebmRemuxCmd
                Verifier = Verify.verifyWebm
                OutputExt = OutputExtension.webm
                ErrorVerb = "WebM remuxing"
                Label = "webm"
                CompletionVerb = "optimised" } ])
            .ToFrozenDictionary()

    let forMode (mode: Mode) = configs[mode]

    let label (mode: Mode) = configs[mode].Label

    let completionVerb (mode: Mode) = configs[mode].CompletionVerb
