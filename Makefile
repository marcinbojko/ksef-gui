.PHONY: all build clean run

all: build

###############################################################################
S = src/KSeFCli
SOURCES := $(shell find $(S) \( -path $(S)/obj -o -path $(S)/bin \) -prune -o \( -type f \( -name '*.cs' -o -name '*.csproj' \) -print \) )
B = $(S)/obj
$(B)/build: $(SOURCES)
	dotnet build $(S)
	@mkdir -p $(dir $@) && touch $@
$(B)/format: $(SOURCES)
	dotnet format $(S)
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
	. .git/.env && ./cli TokenAuth | jq . >/dev/null
	echo SUCCESS
clean:
	dotnet clean $(S)

