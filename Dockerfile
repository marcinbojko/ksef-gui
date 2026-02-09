FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy project files first for better layer caching on restore
COPY thirdparty/ksef-client-csharp/ thirdparty/ksef-client-csharp/
COPY src/KSeFCli/KSeFCli.csproj src/KSeFCli/

RUN dotnet restore src/KSeFCli/KSeFCli.csproj

# Copy remaining source
COPY src/ src/

# Build all platform targets
RUN set -e && \
    ARGS="-c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true" && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r linux-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r win-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r osx-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r osx-arm64

# --- Build ksef-pdf-generator as SEA (Single Executable Application) ---
FROM node:22-slim AS pdf-build

RUN apt-get update && apt-get install -y --no-install-recommends \
    git ca-certificates curl xz-utils unzip && rm -rf /var/lib/apt/lists/*

# Clone and build ksef-pdf-generator
RUN git clone --depth 1 https://github.com/kamilcuk/ksef-pdf-generator.git /ksef-pdf-generator
WORKDIR /ksef-pdf-generator
RUN npm install && npm run build

# Create SEA entry point with navigator/window shim (required by pdfmake)
RUN printf '%s\n' \
  'if(typeof navigator==="undefined")global.navigator={userAgent:"node"};' \
  'if(typeof window==="undefined")global.window={};' \
  'require("./cli.cjs");' \
  > /ksef-pdf-generator/sea-entry.cjs

# Bundle all dependencies into a single CommonJS file
RUN npx esbuild /ksef-pdf-generator/sea-entry.cjs \
    --bundle --platform=node --format=cjs \
    --outfile=/sea/bundle.cjs

# Generate SEA blob (platform-independent)
RUN echo '{"main":"/sea/bundle.cjs","output":"/sea/sea.blob","disableExperimentalSEAWarning":true}' \
    > /sea/sea-config.json && \
    node --experimental-sea-config /sea/sea-config.json

# Download Node.js binaries for all target platforms
ARG NODE_VERSION=22.13.1
RUN mkdir -p /sea/out && \
    # Linux x64 — use container's own node binary
    cp $(command -v node) /sea/out/ksef-pdf-generator-linux-x64 && \
    # Windows x64
    curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-win-x64.zip" \
      -o /tmp/node-win.zip && \
    unzip -j /tmp/node-win.zip "node-v${NODE_VERSION}-win-x64/node.exe" -d /sea/out/ && \
    mv /sea/out/node.exe /sea/out/ksef-pdf-generator-win-x64.exe && \
    rm /tmp/node-win.zip && \
    # macOS x64
    curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-x64.tar.gz" \
      | tar -xz -C /tmp && \
    cp /tmp/node-v${NODE_VERSION}-darwin-x64/bin/node /sea/out/ksef-pdf-generator-osx-x64 && \
    rm -rf /tmp/node-v${NODE_VERSION}-darwin-x64 && \
    # macOS arm64
    curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-darwin-arm64.tar.gz" \
      | tar -xz -C /tmp && \
    cp /tmp/node-v${NODE_VERSION}-darwin-arm64/bin/node /sea/out/ksef-pdf-generator-osx-arm64 && \
    rm -rf /tmp/node-v${NODE_VERSION}-darwin-arm64

# Inject SEA blob into all platform binaries
RUN npx postject /sea/out/ksef-pdf-generator-linux-x64 NODE_SEA_BLOB /sea/sea.blob \
      --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2 --overwrite && \
    npx postject /sea/out/ksef-pdf-generator-win-x64.exe NODE_SEA_BLOB /sea/sea.blob \
      --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2 --overwrite && \
    npx postject /sea/out/ksef-pdf-generator-osx-x64 NODE_SEA_BLOB /sea/sea.blob \
      --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2 --overwrite \
      --macho-segment-name NODE_SEA && \
    npx postject /sea/out/ksef-pdf-generator-osx-arm64 NODE_SEA_BLOB /sea/sea.blob \
      --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2 --overwrite \
      --macho-segment-name NODE_SEA

# --- Final stage: all binaries, no Node.js runtime needed ---
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libssl3 ca-certificates && rm -rf /var/lib/apt/lists/*

WORKDIR /output

COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/linux-x64/publish/   linux-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/win-x64/publish/     win-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/osx-x64/publish/     osx-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/osx-arm64/publish/   osx-arm64/

COPY --from=pdf-build /sea/out/ksef-pdf-generator-linux-x64   linux-x64/ksef-pdf-generator
COPY --from=pdf-build /sea/out/ksef-pdf-generator-win-x64.exe win-x64/ksef-pdf-generator.exe
COPY --from=pdf-build /sea/out/ksef-pdf-generator-osx-x64     osx-x64/ksef-pdf-generator
COPY --from=pdf-build /sea/out/ksef-pdf-generator-osx-arm64   osx-arm64/ksef-pdf-generator

# Symlink linux binary for convenience
RUN ln -s /output/linux-x64/ksefcli /usr/local/bin/ksefcli

ENTRYPOINT ["ksefcli"]
CMD ["Gui", "--lan", "--pdf"]
