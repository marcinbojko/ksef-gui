.PHONY: all build clean run

all: build

build:
	dotnet restore ksefcli.sln
	dotnet build ksefcli.sln

run: build
	dotnet run --project src/KSeFCli --

clean:
	dotnet clean

solution:
	dotnet new sln -o ksefcli.sln
