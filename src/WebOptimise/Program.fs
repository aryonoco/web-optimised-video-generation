module WebOptimise.Program

open SpectreCoff

[<EntryPoint>]
let main argv =
    try
        WebOptimise.Cli.run(argv).GetAwaiter().GetResult()
    with :? System.OperationCanceledException ->
        E "\nInterrupted by user" |> toConsole
        130
