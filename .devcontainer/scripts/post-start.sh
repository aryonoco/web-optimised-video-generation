#!/usr/bin/env bash
set -e
shopt -s inherit_errexit

# Uses parameter expansion rather than head/sed to avoid SC2312 subshell warnings
get_version() {
    local output
    output=$("${@}" 2>/dev/null) || { echo "N/A"; return; }
    echo "${output%%$'\n'*}"
}

echo ""
echo "=== F# Tools Environment ==="
echo ""

ver_dotnet=$(get_version dotnet --version)
ver_ffmpeg=$(get_version ffmpeg -version)
ver_ffmpeg="${ver_ffmpeg#ffmpeg version }"
ver_ffmpeg="${ver_ffmpeg%% *}"
ver_ffprobe=$(get_version ffprobe -version)
ver_ffprobe="${ver_ffprobe#ffprobe version }"
ver_ffprobe="${ver_ffprobe%% *}"
ver_just=$(get_version just --version)
ver_ghcli=$(get_version gh --version)
ver_fantomas=$(get_version dotnet fantomas --version)
ver_fsharplint=$(get_version dotnet fsharplint --version)
ver_cspell=$(get_version cspell --version)

echo "Tools:"
echo "  .NET SDK:     ${ver_dotnet}"
echo "  ffmpeg:       ${ver_ffmpeg}"
echo "  ffprobe:      ${ver_ffprobe}"
echo "  just:         ${ver_just}"
echo "  GitHub CLI:   ${ver_ghcli}"
echo "  fantomas:     ${ver_fantomas}"
echo "  fsharplint:   ${ver_fsharplint}"
echo "  cspell:       ${ver_cspell}"
echo ""

if gh auth status &>/dev/null 2>&1; then
    echo "GitHub CLI: Authenticated"
else
    echo "GitHub CLI: Not authenticated (run 'gh auth login')"
fi

echo ""
echo "=== Quick Commands ==="
echo "  just --list   - Show all available commands"
echo "  just ci       - Run full CI pipeline"
echo "  just fmt      - Format F# code"
echo "  just build    - Debug build"
echo "  just release  - Release build"
echo ""
