.PHONY: all build clean run

all: build

build:
	dotnet build src/KSeFCli

run: build
	dotnet run --project src/KSeFCli --

format:
	dotnet format src/KSeFCli

test: format build
	dotnet run --project src/KSeFCli -- --help

clean:
	dotnet clean src/KSeFCli
