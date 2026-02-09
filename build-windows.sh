#!/usr/bin/env bash
set -euo pipefail

echo "Building ksefcli for Windows (win-x64)..."

dotnet publish src/KSeFCli/KSeFCli.csproj \
    -c Release \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:InvariantGlobalization=true \
    -r win-x64

echo "Copying binary to dist/..."
mkdir -p dist
cp src/KSeFCli/bin/Release/net10.0/win-x64/publish/ksefcli.exe dist/

echo "✓ Build complete!"
ls -lh dist/ksefcli.exe
