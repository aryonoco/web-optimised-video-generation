---
paths:
  - "src/**/*.fs"
  - "src/**/*.fsi"
---

# Functional Programming Principles

All changes must preserve these principles:

- **Railway Oriented Programming**: propagate errors via `Result<'T, AppError>` and `taskResult`/`result` computation expressions. Throw only for truly exceptional failures, not domain errors.
- **Functional Core, Imperative Shell**: pure modules (Constants, Domain, Commands, ProbeParse, Ebml, ModeConfig, Discovery) contain zero side effects. I/O belongs exclusively in Shell, Process, Verify, Display, and Cli.
- **Parse, Don't Validate**: use branded types with private constructors and smart constructors to make illegal states unrepresentable. Use discriminated unions for closed sets (e.g. `ShellError`, `OutputPath`, `Mode`).
- **Immutability by default**: no `mutable` in pure modules. Mutation is acceptable only at I/O boundaries or for performance-critical low-level code.
- **Total functions**: return `Result`, `Option`, or `ValueOption`. Use active patterns for safe parsing.
- **Composition over inheritance**: pipeline operators, `map`/`bind`/`fold`, and computation expressions. No classes, no inheritance hierarchies.
- **Value types for performance**: `[<Struct>]` DUs and records, struct tuples for multi-value returns where appropriate.
