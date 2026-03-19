---
paths:
  - "src/WebOptimise/**/*.fs"
  - "src/WebOptimise/**/*.fsi"
  - "src/WebOptimise/**/*.fsproj"
---

# WebOptimise

CLI tool that optimises video files for progressive web delivery using ffmpeg/ffprobe.

## Run

dotnet run --project src/WebOptimise -- <paths> [-m remux|encode] [-n] [-f]

Requires `ffmpeg` and `ffprobe` on PATH (installed via `src/WebOptimise/mise.toml`).

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

### Gotchas

- Compile order in `.fsproj` defines module dependency order — reordering files can break the build if not careful.
- `web-optimised/` output directory is created alongside source files.
