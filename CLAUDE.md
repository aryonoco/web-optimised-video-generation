# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## CRITICAL: NO AI ATTRIBUTION

**DO NOT** mention AI, LLM, Claude, Claude Code, Anthropic, "generated", "assisted", or any similar reference **anywhere** — not in code, comments, commit messages, docstrings, PR descriptions, or any other artifact. No `Co-Authored-By`, no `Generated with`, no `AI-assisted`, nothing.

## Build & Run

```bash
dotnet build                                          # debug build
dotnet publish -c Release                             # release (single-file, trimmed)
dotnet run --project src/WebOptimise -- <paths> [-m remux|encode] [-n] [-f]
```

Requires .NET 10.0 SDK. Runtime dependencies: `ffmpeg` and `ffprobe` on PATH.

## Format & Lint

```bash
dotnet tool restore                                   # install fantomas, fsharplint, fsharp-analyzers
dotnet fantomas src/                                  # format all F# sources
dotnet fsharplint lint WebOptimise.slnx               # lint
```

All warnings are errors (`TreatWarningsAsErrors`). Nullable reference types and overflow checks are enabled.

## Workflow: after each major phase

After completing each significant section of work (a refactor, a new feature, a bug fix), run the full quality pipeline and fix every issue — never suppress or ignore errors/warnings:

1. `dotnet fantomas src/` — format
2. `dotnet fsharplint lint WebOptimise.slnx` — lint; fix all reported issues
3. `dotnet build -c Release` — must produce zero warnings and zero errors
4. `dotnet fsharp-analyzers --project src/WebOptimise/WebOptimise.fsproj --analyzers-path ~/.nuget/packages/g-research.fsharp.analyzers/0.22.0/analyzers/dotnet/fs/ --analyzers-path ~/.nuget/packages/ionide.analyzers/0.15.0/analyzers/dotnet/fs/ --verbosity d` — run both analyzer sets; fix all issues they report

## Architecture

CLI tool that optimises video files for progressive web delivery using ffmpeg. Three processing modes:

- **Remux** (MP4/M4V/MOV): copy streams, add `faststart` atom, strip metadata
- **Encode** (MP4): re-encode to H.264 High/4.0 via x264 (CRF 25, preset slower), 2-second keyframe intervals
- **Webm** (MKV with AV1+Opus): remux to WebM, validate EBML Cues element is at front

### Pipeline flow

`Cli.fs` orchestrates: parse args (Argu) → validate ffmpeg/ffprobe → `Discovery.fs` finds files and resolves mode per file → `ProbeParse.fs` runs ffprobe and parses JSON into domain records → `Display.fs` shows analysis → `Process.fs` runs ffmpeg with streaming progress → `Verify.fs` post-encode checks (faststart, keyframe spacing, WebM Cues position) → `Display.fs` summary.

### Module dependency order

Matches the compile order in `.fsproj`: Constants → Domain → Shell → ProbeParse → Commands → Ebml → Verify → ModeConfig → Discovery → Process → Display → Cli → Program.

`ModeConfig.fs` ties modes to their command builders, verifiers, and output extensions — the dispatch table that avoids match expressions scattered across modules.

### Key patterns

- **Branded types** in `Domain.fs`: `MediaFilePath`, `OutputExtension` — private constructors, smart constructors returning `Result`
- **`Result<'T, AppError>`** throughout; `taskResult` CE from FsToolkit.ErrorHandling for async+Result composition
- **`ValueOption` (voption)** for optional fields (audio stream, bitrate) — struct option for zero-alloc
- **Active patterns** (`Int`, `Float`, `Json.Prop`, `Json.Str`) for parsing without external JSON libraries
- **`Commands.fs` is pure**: builds ffmpeg argument lists with no side effects; `Shell.fs`/`Process.fs` handle execution
- **`Ebml.fs`**: hand-rolled WebM/Matroska binary parser (VINT encoding, element ID scanning) with `[<TailCall>]` recursive descent

### Output structure

Processed files go into a `web-optimised/` subdirectory alongside the source, preserving the original filename with the appropriate extension (.mp4 or .webm).
