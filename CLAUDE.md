# CLAUDE.md

Monorepo of F# CLI tools on .NET 10.0. Each tool lives in `src/<ToolName>/` as a self-contained project.

## CRITICAL: NO AI ATTRIBUTION

**DO NOT** mention AI, LLM, Claude, Claude Code, Anthropic, "generated", "assisted", or any similar reference **anywhere** — not in code, comments, commit messages, docstrings, PR descriptions, or any other artifact. No `Co-Authored-By`, no `Generated with`, no `AI-assisted`, nothing.

Preserve strict FP principles in all changes — see `.claude/rules/fp-principles.md`.

## Build & Run

```bash
dotnet build                                          # build all tools
dotnet build src/<ToolName>                           # build one tool
dotnet publish src/<ToolName> -c Release              # release one tool
dotnet run --project src/<ToolName> -- <args>          # run one tool
```

Requires .NET 10.0 SDK. Individual tools may require additional dependencies (see per-tool `mise.toml`).

## Quality Workflow

All warnings are errors (`TreatWarningsAsErrors`). Run `dotnet tool restore` once to install tools.

After each significant change, run in order and fix every issue:

1. `dotnet fantomas src/` — format
2. `dotnet fantomas --check src/` — verify (exit 0 = OK, 1 = needs formatting, 99 = error)
3. `dotnet fsharplint lint FSharpTools.slnx` — lint
4. `dotnet build -c Release` — zero warnings/errors (G-Research + Ionide analyzers included via FSharp.Analyzers.Build)

## Tools

- **WebOptimise** (`src/WebOptimise/`): optimise video files for progressive web delivery. See `.claude/rules/weboptimise.md` for architecture context.

## Shared Infrastructure

- `Directory.Build.props` — compiler settings, trimming, publishing (applies to all projects)
- `Directory.Build.targets` — F# analyzer config (applies to all projects)
- `Directory.Packages.props` — central NuGet package version management
- Tool-specific config (TrimmerRootAssembly, external deps) belongs in each `.fsproj` and `src/<Tool>/mise.toml`

## Gotchas

- Compile order in each `.fsproj` defines module dependency order — reordering files can break the build.
- `dotnet tool restore` is required before the first quality workflow run.
- All analyzer findings (G-Research + Ionide) are treated as build errors, not warnings.
- Each tool may have its own `mise.toml` for tool-specific dependencies (e.g., ffmpeg for WebOptimise).
