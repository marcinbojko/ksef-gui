# Use the .NET 10.0 SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

# Copy the solution file and project files
COPY ksefcli.sln .
COPY src/KSeFCli/KSeFCli.csproj ./src/KSeFCli/
COPY thirdparty/ksef-client-csharp/KSeF.Client/KSeF.Client.csproj ./thirdparty/ksef-client-csharp/KSeF.Client/
COPY thirdparty/ksef-client-csharp/KSeF.Client.Core/KSeF.Client.Core.csproj ./thirdparty/ksef-client-csharp/KSeF.Client.Core/

# Restore dependencies
RUN dotnet restore ksefcli.sln

# Copy the rest of the source code
COPY . .

# Publish the application as a self-contained single file
RUN dotnet publish src/KSeFCli/KSeFCli.csproj -c Release -o /app/publish -r linux-x64 --self-contained true /p:PublishSingleFile=true

# Use the .NET 10 runtime dependencies for the final image
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final

WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Set the entrypoint for the application
ENTRYPOINT ["./KSeFCli"]
