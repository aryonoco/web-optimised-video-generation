# CLAUDE.md

CLI tool that optimises video files for progressive web delivery using ffmpeg/ffprobe. F# on .NET 10.0.

## CRITICAL: NO AI ATTRIBUTION

**DO NOT** mention AI, LLM, Claude, Claude Code, Anthropic, "generated", "assisted", or any similar reference **anywhere** — not in code, comments, commit messages, docstrings, PR descriptions, or any other artifact. No `Co-Authored-By`, no `Generated with`, no `AI-assisted`, nothing.

Preserve strict FP principles in all changes — see `.claude/rules/fp-principles.md`.

## Build & Run

```bash
dotnet build                                          # debug build
dotnet publish -c Release                             # release (single-file, trimmed)
dotnet run --project src/WebOptimise -- <paths> [-m remux|encode] [-n] [-f]
```

Requires .NET 10.0 SDK. `ffmpeg` and `ffprobe` must be on PATH.

## Quality Workflow

All warnings are errors (`TreatWarningsAsErrors`). Run `dotnet tool restore` once to install tools.

After each significant change, run in order and fix every issue:

1. `dotnet fantomas src/` — format
2. `dotnet fantomas --check src/` — verify (exit 0 = OK, 1 = needs formatting, 99 = error)
3. `dotnet fsharplint lint WebOptimise.slnx` — lint
4. `dotnet build -c Release` — zero warnings/errors (G-Research + Ionide analyzers included via FSharp.Analyzers.Build)

## Architecture

Three processing modes:

- **Remux** (MP4/M4V/MOV): copy streams, add `faststart` atom, strip metadata
- **Encode** (MP4): re-encode H.264 High/4.0 via x264 (CRF 25, preset slower), 2-second keyframe intervals
- **Webm** (MKV with AV1+Opus): remux to WebM, validate EBML Cues element at front

Pipeline: parse args (Argu) -> validate tools -> discover/deduplicate files -> probe (ffprobe JSON) -> display analysis -> process with progress -> verify -> summary.

Output goes to `web-optimised/` subdirectory alongside source, preserving filename with .mp4 or .webm extension.

### Key Design Decisions

- **Functional Core, Imperative Shell**: Pure modules (Constants, Domain, Commands, ProbeParse, Ebml, ModeConfig, Discovery) contain zero side effects. I/O lives in Shell, Process, Verify, Display, Cli.
- **Env record** (`Capabilities.fs`): all I/O primitives injected via `Env` — other modules receive `env: Env`, never call Shell directly.
- **ModeConfig dispatch table**: maps each mode to its command builder, verifier, and output extension.

## Gotchas

- Compile order in `.fsproj` defines module dependency order — reordering files can break the build if not careful.
- `dotnet tool restore` is required before the first quality workflow run.
- All analyzer findings (G-Research + Ionide) are treated as build errors, not warnings.
