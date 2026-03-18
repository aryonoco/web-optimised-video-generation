module WebOptimise.Program

[<EntryPoint>]
let main argv =
    try
        WebOptimise.Cli.run(argv).GetAwaiter().GetResult()
    with :? System.OperationCanceledException ->
        Spectre.Console.AnsiConsole.MarkupLine "\n[yellow]Interrupted by user[/]"
        130
