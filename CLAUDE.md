# KSeFCli - Project Context for Claude

## Build & Run

```bash
make build                    # Build the project (.NET 10.0 required)
git submodule update --init --recursive  # Required: pulls thirdparty/ksef-client-csharp
./ksefcli TokenAuth           # Authenticate before using API commands
./ksefcli Gui -o /tmp/faktury --pdf  # Launch browser GUI
./ksefcli Gui --lan           # GUI accessible from LAN (all network interfaces)
make build-osx-arm64          # Build for macOS Apple Silicon (M1/M2/M3/M4)
make build-osx-x64            # Build for macOS Intel
make build-osx                # Build for both macOS architectures
```

## Architecture

- C# .NET 10.0 CLI using `CommandLineParser` for verb-based commands
- Command hierarchy: `IGlobalCommand` → `IWithConfigCommand` → specific commands
- Auth: long-term KSeF token in `ksefcli.yaml` → access token (~15min) + refresh token (up to 7 days)
- Token cache: `~/.cache/ksefcli/tokenstore.json`, auto-refresh in `GetAccessToken()` (IWithConfigCommand.cs:231)
- KSeF API client: `thirdparty/ksef-client-csharp/` (git submodule)
- KSeF docs: `thirdparty/ksef-docs/` (uwierzytelnianie.md, sesja-interaktywna.md)

## Recent Changes (Feb 2026 session)

### 1. pdfmake Navigator Fix (`navigator-shim.cjs`, `XML2PDFCommand.cs`)
- **Problem**: `npx ksef-pdf-generator` uses pdfmake browser build which requires `navigator` global
- **Fix**: Created `navigator-shim.cjs` that polyfills `navigator` and `window` globals
- `XML2PDFCommand.cs` passes `NODE_OPTIONS=--require "<shimPath>"` to npx subprocess
- `KSeFCli.csproj` copies `navigator-shim.cjs` to output directory

### 2. Date Keywords (`ParseDate.cs`)
- Added `thismonth`/`this-month` → first day of current month
- Added `lastmonth`/`last-month` → first day of previous month
- Existing `-Ndays`, `-Nmonths` etc. still work via shell `date` command

### 3. Interactive Browser GUI (`GuiCommand.cs`, `WebProgressServer.cs`)
- New `ksefcli Gui` command with `--outputdir`, `--pdf`, `--useInvoiceNumber`, `--lan` options
- Registered in `Program.cs` ParseArguments list
- `WebProgressServer.cs`: HttpListener-based server with endpoints:
  - `GET /` — HTML page with search form, results table, folder picker, currency filter
  - `GET /events` — SSE stream for download progress
  - `GET /prefs` — Load saved GUI preferences (output dir)
  - `POST /prefs` — Save GUI preferences
  - `POST /auth` — Force re-authentication (OnAuth callback)
  - `POST /search` — Search invoices (OnSearch callback with SearchParams record)
  - `POST /download` — Download all cached invoices (OnDownload callback)
  - `GET /browse?path=` — Directory listing for folder picker
  - `POST /mkdir` — Create new directory
  - `GET /invoice-details?idx=N` — Fetch and parse invoice XML details (OnInvoiceDetails callback)
  - `POST /quit` — Shut down server (OnQuit callback)
- `GuiCommand.cs`:
  - `SearchAsync`: Builds InvoiceQueryFilters from SearchParams, paginates QueryInvoiceMetadataAsync (page size 100), returns flattened invoice data
  - `DownloadAsync(DownloadParams)`: Downloads to temp workdir, copies only desired formats to output dir. SSE progress, configurable export formats (XML/PDF/JSON), optional custom filenames
  - `BuildFileName`: Supports custom scheme `date-seller-currency-ksef` or default KsefNumber/InvoiceNumber
  - `AuthAsync`: Forces re-auth via `Auth()`, saves to token store, returns expiry time
  - `InvoiceDetailsAsync`: Fetches invoice XML via API, parses with XDocument (namespace `http://crd.gov.pl/wzor/2025/06/25/13775/`), extracts header (DataWytworzeniaFa, SystemInfo, schema), amounts, addresses, line items (FaWiersz), additional descriptions (DodatkowyOpis), period, invoice type

### GUI Features
- **Profile picker**: Dropdown in search form showing all profiles from `ksefcli.yaml` as `name (NIP: xxx)`. Selected profile saved to prefs and restored on next GUI launch. If CLI `--active` is not set, uses saved profile. Changing profile mid-session saves for next restart (auth is tied to session profile). Disabled when only one profile exists
- **Search form**: Profile dropdown, SubjectType dropdown, month pickers for From/To (min: 2026-02, default: current month), DateType dropdown
- **Results table**: Checkbox + KSeF Number, Invoice Number, Issue Date, Seller, Buyer, Gross Amount, Currency — sortable by clicking headers
- **Invoice selection**: Per-row checkboxes, "Zaznacz wszystkie" / "Odznacz wszystkie" buttons, "Pobierz zaznaczone" + "Pobierz wszystkie" buttons
- **Currency filter**: Dynamic chips built from search results, toggle on/off, off by default
- **Filename option**: Checkbox "Nazwy plikow: data-sprzedawca-waluta-ksef" for custom filename scheme (YYYY-MM-DD-SellerName-Currency-KsefNumber), spaces in seller name replaced with underscores
- **Folder picker**: Modal with directory browser, parent navigation, create new folder
- **Auth button**: Orange "Autoryzuj" button for token refresh without restarting
- **Export format**: Checkboxes for XML (default on), PDF (default on), JSON (default off) — controls which file types are saved
- **Separate by NIP**: Checkbox "Separuj po NIP" in preferences — when enabled, downloads go to `{outputDir}/{profileNip}/` subfolder. NIP read from active profile in `ksefcli.yaml` via `Config().Nip`. Displayed in checkbox label for reference. Works with profile picker: each profile has its own NIP, so multi-company setups get separate output folders
- **Persistent preferences**: All settings (output dir, export format, custom filenames, separate by NIP, selected profile) saved to `~/.cache/ksefcli/gui-prefs.json`, restored on next GUI launch. Output dir and folder picker moved to preferences panel. Saved on checkbox change and folder selection
- **Invoice details**: Per-row details button (magnifying glass icon), click opens popover with parsed XML data: header info (creation date, system, schema), amounts, addresses, line items table, additional descriptions. Results cached per session. Popover closes on Escape, outside click, or close button
- **Quit button**: Red "Zakoncz" button in search bar, calls `POST /quit`, triggers `Environment.Exit(0)` via OnQuit delegate
- **Save Prefs button**: Explicit "Zapisz preferencje" button in preferences panel (prefs also auto-save on checkbox changes and folder selection)
- **Responsive layout**: Full-width layout for 4K screens, no max-width cap
- **LAN access**: `--lan` CLI option makes GUI accessible from all network interfaces (not just localhost). Uses `http://+:{port}/` prefix for HttpListener. Useful for accessing GUI from other devices on the same network
- **Download progress**: SSE-driven progress bar, per-row status icons (spinner → checkmark)
- **Records**: `SearchParams(SubjectType, From, To, DateType)`, `DownloadParams(OutputDir, SelectedIndices, CustomFilenames, ExportXml, ExportJson, ExportPdf, SeparateByNip)`

### 4. macOS Build Support (`.gitlab-ci.yml`, `Makefile`)
- CI jobs for `osx-x64` (Intel) and `osx-arm64` (Apple Silicon) — self-contained single-file publish
- Makefile targets: `build-osx-x64`, `build-osx-arm64`, `build-osx` (both)
- Output binaries: `ksefcli-osx-x64`, `ksefcli-osx-arm64`
- Cross-compilation from Linux CI runner (no Mac runner required)

## Key Files

| File | Role |
|------|------|
| `GuiCommand.cs` | Gui verb command, wires WebProgressServer callbacks |
| `WebProgressServer.cs` | HTTP server, SSE, embedded HTML/CSS/JS |
| `IWithConfigCommand.cs` | Base class with auth, token cache, GetAccessToken |
| `PobierzFaktury.cs` | CLI download command (no GUI, clean CLI-only) |
| `SzukajFakturCommand.cs` | CLI search command base |
| `XML2PDFCommand.cs` | XML→PDF via npx ksef-pdf-generator |
| `ParseDate.cs` | Date string parsing (keywords + shell `date` fallback) |
| `TokenStore.cs` | Token cache read/write with file locking |
| `navigator-shim.cjs` | Node.js polyfill for pdfmake browser globals |
| `Program.cs` | Entry point, verb registration |
