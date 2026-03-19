set shell := ["bash", "-euo", "pipefail", "-c"]

# .NET RID auto-detection from just's os()/arch() built-ins
dotnet_os := if os() == "macos" { "osx" } else if os() == "windows" { "win" } else { os() }
dotnet_arch := if arch() == "x86_64" { "x64" } else if arch() == "aarch64" { "arm64" } else { arch() }
default_rid := dotnet_os + "-" + dotnet_arch

# Show available commands
default:
    @just --list

# Install .NET tools and restore packages
setup:
    dotnet tool restore
    dotnet restore

# Format F# code with fantomas
fmt:
    dotnet fantomas src/

# Check formatting without modifying (exit 0=OK, 1=needs formatting, 99=error)
fmt-check:
    dotnet fantomas --check src/

# Lint with fsharplint
lint:
    dotnet fsharplint lint WebOptimise.slnx

# Debug build
build:
    dotnet build

# Release build (zero warnings/errors required)
build-release:
    dotnet build -c Release

# Publish release (single-file, trimmed, self-contained)
release rid=default_rid:
    dotnet publish -c Release -r {{ rid }}
    @echo "Published: src/WebOptimise/bin/Release/net10.0/{{ rid }}/publish/WebOptimise"

# Clean build artifacts
clean:
    dotnet clean
    rm -rf bin/ obj/ src/*/bin/ src/*/obj/

# Full CI pipeline: format check, lint, release build
ci: fmt-check lint build-release

# Show tool versions
versions:
    @echo "=== Tool Versions ==="
    @dotnet --version
    @ffmpeg -version 2>&1 | head -1
    @ffprobe -version 2>&1 | head -1
    @just --version
    @dotnet fantomas --version 2>&1 | head -1
