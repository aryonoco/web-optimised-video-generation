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

## Quality workflow: after each major phase

All warnings are errors (`TreatWarningsAsErrors`). Nullable reference types and overflow checks are enabled. Run `dotnet tool restore` once to install fantomas, fsharplint, and fsharp-analyzers.

After completing each significant section of work (a refactor, a new feature, a bug fix), run the full quality pipeline and fix every issue — never suppress or ignore errors/warnings:

1. `dotnet fantomas src/` — format all source files
2. `dotnet fantomas --check src/` — verify formatting (exit 0 = OK, exit 1 = needs formatting, exit 99 = error)
3. `dotnet fsharplint lint WebOptimise.slnx` — lint; fix all reported issues
4. `dotnet build -c Release` — zero warnings/errors (includes all 25 G-Research + Ionide analyzers automatically via FSharp.Analyzers.Build; all findings treated as errors)

## Architecture

CLI tool that optimises video files for progressive web delivery using ffmpeg. Three processing modes:

- **Remux** (MP4/M4V/MOV): copy streams, add `faststart` atom, strip metadata
- **Encode** (MP4): re-encode to H.264 High/4.0 via x264 (CRF 25, preset slower), 2-second keyframe intervals
- **Webm** (MKV with AV1+Opus): remux to WebM, validate EBML Cues element is at front

### Pipeline flow

`Cli.fs` orchestrates: parse args (Argu) → validate ffmpeg/ffprobe → `Shell.fs` resolves/enumerates paths → `Discovery.fs` filters and deduplicates files, resolves mode per file → `ProbeParse.fs` parses ffprobe JSON into domain records → `Display.fs` shows analysis → `Process.fs` runs ffmpeg with streaming progress → `Verify.fs` post-encode checks (faststart, keyframe spacing, WebM Cues position) → `Display.fs` summary.

### Module dependency order

Matches the compile order in `.fsproj`: Constants → Domain → Shell → ProbeParse → Commands → Ebml → Verify → ModeConfig → Discovery → Process → Display → Cli → Program.

`ModeConfig.fs` ties modes to their command builders, verifiers, and output extensions — the dispatch table that avoids match expressions scattered across modules.

### Functional principles

This codebase follows strict functional programming principles — all changes must preserve them. Use **Railway Oriented Programming**: propagate errors via `Result<'T, AppError>` and `taskResult`/`result` computation expressions; never throw exceptions for domain errors. Maintain the **Functional Core, Imperative Shell** separation: pure modules (Constants, Domain, Commands, ProbeParse, Ebml, ModeConfig, Discovery) must contain zero side effects; I/O belongs exclusively in Shell, Process, Verify, Display, and Cli. Shell provides all I/O primitives (`resolveInputPath`, `enumerateFiles`, `runBuffered`, `runStreaming`, `runExists`). Apply **Parse, Don't Validate**: use branded types with private constructors and smart constructors to make illegal states unrepresentable; use discriminated unions for closed sets (e.g. `ShellError`, `OutputPath`, `ResolvedPath`, `FfmpegCmd`). Keep **immutability by default**: no `mutable` in pure modules; mutation is acceptable only at I/O boundaries or for performance-critical low-level code. Write **total functions**: return `Result`, `Option`, or `ValueOption` — never throw for expected failures; use active patterns for safe parsing. Prefer **composition over inheritance**: pipeline operators, `map`/`bind`/`fold`, and computation expressions — no classes, no inheritance hierarchies. Use **value types for performance** where appropriate: `[<Struct>]` DUs and records, `voption` for optional fields, struct tuples for multi-value returns.

### Output structure

Processed files go into a `web-optimised/` subdirectory alongside the source, preserving the original filename with the appropriate extension (.mp4 or .webm).
