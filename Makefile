.PHONY: all build clean run

all: build

build:
	dotnet restore ksefcli.sln
	dotnet build ksefcli.sln

run: build
	dotnet run --project src/KSeFCli --

format:
	dotnet format src/KSeFCli

test:
	dotnet format src/KSeFCli --verify-no-changes

clean:
	dotnet clean ksefcli.sln
