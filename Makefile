.PHONY: all build clean run
null  :=
space := $(null) #
comma := ,
join_comma = $(subst $(space),$(comma),$(1))

all: build

###############################################################################
S = src/KSeFCli
SOURCES := $(shell find $(S) \( -path $(S)/obj -o -path $(S)/bin \) -prune -o \( -type f \( -name '*.cs' -o -name '*.csproj' \) -print \) )
B = $(S)/obj
$(B)/build: $(SOURCES)
	dotnet build $(S)
	@mkdir -p $(dir $@) && touch $@
$(B)/format: $(SOURCES)
	dotnet format $(S) -v d
	@mkdir -p $(dir $@) && touch $@
###############################################################################

.PHONY: build format run test clean sources
build: $(B)/build
format: $(B)/format
sources:
	@echo $(SOURCES)
run: build
	dotnet run --project $(S) --
test: format build
	dotnet run --project $(S) -- --help
	./cli -c .git/ksefcli.yaml TokenAuth | jq . >/dev/null
	echo SUCCESS
clean:
	dotnet clean $(S)
test-format:
	dotnet format $(S) -v d --verify-no-changes

###############################################################################

DOTNET_PUBLISH = dotnet publish src/KSeFCli/KSeFCli.csproj -c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true
GITLAB_BUILD_CMD := $(shell sed -n 's/.*- \(dotnet publish\)/\1/p' .gitlab-ci.yml)
build-static:
	$(GITLAB_BUILD_CMD)
docker-build-static:
	docker run -ti --rm -u "$(shell id -u):$(shell id -g)" -v $(CURDIR):$(CURDIR) -w $(CURDIR) \
		mcr.microsoft.com/dotnet/sdk:10.0 $(GITLAB_BUILD_CMD)
build-osx-x64:
	$(DOTNET_PUBLISH) -r osx-x64
build-osx-arm64:
	$(DOTNET_PUBLISH) -r osx-arm64
build-osx: build-osx-x64 build-osx-arm64

DOCKER_TAG ?= ksefcli:latest
docker-build-all:
	docker build -t $(DOCKER_TAG) .
docker-extract: docker-build-all
	@mkdir -p dist
	docker create --name ksefcli-extract $(DOCKER_TAG) 2>/dev/null || true
	docker cp ksefcli-extract:/output/linux-x64/ksefcli dist/ksefcli
	docker cp ksefcli-extract:/output/win-x64/ksefcli.exe dist/ksefcli.exe
	docker cp ksefcli-extract:/output/osx-x64/ksefcli dist/ksefcli-osx-x64
	docker cp ksefcli-extract:/output/osx-arm64/ksefcli dist/ksefcli-osx-arm64
	docker rm ksefcli-extract
	@echo "Binaries extracted to dist/"
	@echo "Note: PDF generation requires Node.js (npx) to be installed at runtime"
	@ls -lh dist/
