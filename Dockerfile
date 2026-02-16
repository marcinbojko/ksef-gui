ARG APP_VERSION="dev"

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG APP_VERSION

WORKDIR /src

# Copy project files first for better layer caching on restore
COPY thirdparty/ksef-client-csharp/ thirdparty/ksef-client-csharp/
COPY src/KSeFCli/KSeFCli.csproj src/KSeFCli/

RUN dotnet restore src/KSeFCli/KSeFCli.csproj

# Copy remaining source
COPY src/ src/

# Build all platform targets
RUN set -e && \
    ARGS="-c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true -p:SourceRevisionId=${APP_VERSION}" && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r linux-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r win-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r osx-x64 && \
    dotnet publish src/KSeFCli/KSeFCli.csproj $ARGS -r osx-arm64

# --- Final stage: ksefcli binaries (PDF renderer is built-in, no external tools needed) ---
FROM debian:bookworm-slim

ARG APP_VERSION

LABEL version="${APP_VERSION}"
LABEL release="ksefcli"
LABEL org.opencontainers.image.version="${APP_VERSION}"
LABEL org.opencontainers.image.title="ksefcli"
LABEL org.opencontainers.image.description="KSeF invoice downloader CLI and GUI"
LABEL org.opencontainers.image.url="https://github.com/marcinbojko/ksef-gui"
LABEL org.opencontainers.image.source="https://github.com/marcinbojko/ksef-gui"
LABEL org.opencontainers.image.licenses="GPL-3.0"

RUN apt-get update && apt-get install -y --no-install-recommends \
    libssl3 ca-certificates curl && rm -rf /var/lib/apt/lists/*

# Create cache and output directories (config dir managed by named volume)
RUN mkdir -p /root/.cache/ksefcli /data

WORKDIR /output

COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/linux-x64/publish/   linux-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/win-x64/publish/     win-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/osx-x64/publish/     osx-x64/
COPY --from=build /src/src/KSeFCli/bin/Release/net10.0/osx-arm64/publish/   osx-arm64/

# Symlink linux binary for convenience
RUN ln -s /output/linux-x64/ksefcli /usr/local/bin/ksefcli

ENTRYPOINT ["ksefcli"]
CMD ["Gui", "--lan", "--pdf"]
