#!/usr/bin/env bash
# build.sh — build ksefcli self-contained binary for testing outside GitHub
#
# Usage:
#   ./build.sh                  — build for current platform (auto-detected)
#   ./build.sh linux-x64        — cross-compile for specific target
#   ./build.sh all              — cross-compile all targets
#   ./build.sh --help           — show this help
#
# Output: dist/ksefcli-<target>  (or dist/ksefcli-<target>.exe on Windows)
# Requires: .NET SDK 10+

set -euo pipefail

TARGETS=(linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64)
PROJECT="src/KSeFCli/KSeFCli.csproj"

usage() {
    sed -n '2,12p' "$0" | sed 's/^# //' | sed 's/^#//'
    echo ""
    echo "Available targets: ${TARGETS[*]}"
    exit 0
}

detect_platform() {
    local os arch
    case "$(uname -s)" in
        Linux*)  os="linux" ;;
        Darwin*) os="osx" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;;
    esac
    echo "${os}-${arch}"
}

build_target() {
    local target="$1"
    local version
    version="$(git describe --tags --always 2>/dev/null || echo "dev")"

    echo "==> Building for ${target} (version: ${version})"

    dotnet publish "${PROJECT}" \
        -c Release \
        -r "${target}" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:InvariantGlobalization=true \
        -p:SourceRevisionId="${version}"

    mkdir -p dist
    local src_base="src/KSeFCli/bin/Release/net10.0/${target}/publish"
    if [[ "${target}" == win-* ]]; then
        cp "${src_base}/ksefcli.exe" "dist/ksefcli-${target}.exe"
        echo "    dist/ksefcli-${target}.exe"
        ls -lh "dist/ksefcli-${target}.exe"
    else
        cp "${src_base}/ksefcli" "dist/ksefcli-${target}"
        chmod +x "dist/ksefcli-${target}"
        echo "    dist/ksefcli-${target}"
        ls -lh "dist/ksefcli-${target}"
    fi
}

# --- argument parsing ---

case "${1:-}" in
    --help|-h) usage ;;
    all)
        for t in "${TARGETS[@]}"; do build_target "$t"; done
        echo ""
        echo "Done. Binaries in dist/:"
        ls -lh dist/
        ;;
    "")
        build_target "$(detect_platform)"
        ;;
    *)
        # validate supplied target
        valid=false
        for t in "${TARGETS[@]}"; do [[ "$1" == "$t" ]] && valid=true && break; done
        if ! $valid; then
            echo "Unknown target: $1" >&2
            echo "Valid targets: ${TARGETS[*]}" >&2
            exit 1
        fi
        build_target "$1"
        ;;
esac
