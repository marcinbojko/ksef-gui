<div align="center">

<img src="src/KSeFCli/app.png" width="96" alt="ksefcli logo" />

# ksefcli

**Klient KSeF ze wbudowanym interfejsem przeglądarkowym**<br/>
**KSeF client with a built-in browser GUI**

[![Release](https://img.shields.io/github/v/release/marcinbojko/ksef-gui?include_prereleases&label=release&color=4f8ef7)](https://github.com/marcinbojko/ksef-gui/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/marcinbojko/ksef-gui/ci.yml?branch=main&label=CI)](https://github.com/marcinbojko/ksef-gui/actions/workflows/ci.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/marcinbojko/ksef-gui/codeql.yml?branch=main&label=CodeQL)](https://github.com/marcinbojko/ksef-gui/actions/workflows/codeql.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE.md)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](#-instalacja)

<br/>

[🇵🇱 Polski](#polski) · [🇬🇧 English](#english)

</div>

---

## Polski

> **Fork** projektu [kamilcuk/ksefcli](https://github.com/kamilcuk/ksefcli) autorstwa [Kamila Cukrowskiego](https://github.com/kamilcuk). Oryginalne repozytorium zawiera wersję CLI; ten fork dodaje rozbudowany interfejs przeglądarkowy i dodatkowe funkcje.

`ksefcli` to narzędzie do pobierania faktur z **Krajowego Systemu e-Faktur (KSeF)**. Oprócz CLI posiada wbudowany interfejs przeglądarkowy uruchamiany lokalnie — bez instalowania dodatkowego oprogramowania.

### ✨ Cechy

|                           |                                                                                           |
| ------------------------- | ----------------------------------------------------------------------------------------- |
| 🌐 **GUI w przeglądarce** | Interfejs lokalny dostępny bez instalacji                                                 |
| 📄 **Eksport PDF**        | Natywny renderer (QuestPDF) — bez Node.js, git ani zewnętrznych narzędzi                  |
| 🔄 **Auto-odświeżanie**   | Wyszukiwanie w tle co N minut; powiadomienia o nowych fakturach                           |
| 💾 **Cache SQLite**       | Wyniki wyszukiwania przechowywane lokalnie; przełączanie profili bez ponownego pobierania |
| 🌙 **Tryb ciemny**        | Trzy niezależne tryby: GUI, podgląd faktury, szczegóły                                    |
| 🐳 **Docker**             | Gotowy `docker-compose` z Traefik i Ofelia                                                |
| 🔒 **Bez internetu**      | Walidacja XSD i generowanie PDF działają w pełni offline                                  |

### 📸 Zrzuty ekranu

<div align="center">

![Główny ekran](images/mainscreen.png)
_Główny ekran — lista faktur_

![Preferencje](images/prefs.png)
_Panel preferencji_

![Konfiguracja](images/config.png)
_Edytor konfiguracji_

</div>

### 📦 Instalacja

Pobierz najnowszy plik binarny ze strony [Releases](https://github.com/marcinbojko/ksef-gui/releases).

<details>
<summary><b>🪟 Windows</b></summary>

Umieść `ksefcli-win-x64.exe` w wybranym folderze (możesz zmienić nazwę na `ksefcli.exe`).

Dwukliknięcie pliku uruchamia GUI automatycznie.

</details>

<details>
<summary><b>🍎 macOS</b></summary>

```bash
# Apple Silicon
chmod +x ksefcli-osx-arm64
xattr -dr com.apple.quarantine ksefcli-osx-arm64

# Intel
chmod +x ksefcli-osx-x64
xattr -dr com.apple.quarantine ksefcli-osx-x64
```

> Usunięcie atrybutu kwarantanny jest wymagane — bez tego Gatekeeper blokuje ładowanie natywnych bibliotek.

</details>

<details>
<summary><b>🐧 Linux</b></summary>

```bash
chmod +x ksefcli-linux-x64   # lub ksefcli-linux-arm64
```

</details>

### 🚀 Szybki start

```bash
./ksefcli
# Przeglądarka otwiera się automatycznie pod adresem http://localhost:<port>
```

Przy pierwszym uruchomieniu bez pliku konfiguracyjnego GUI otwiera **kreator konfiguracji** automatycznie.

### ⚙ Plik konfiguracyjny

`ksefcli` szuka pliku `ksefcli.yaml` w następującej kolejności:

| Priorytet | Lokalizacja                                   |
| :-------: | --------------------------------------------- |
|     1     | Flaga `-c /sciezka/do/pliku`                  |
|     2     | Zmienna środowiskowa `KSEFCLI_CONFIG`         |
|     3     | `./ksefcli.yaml` — bieżący katalog            |
|     4     | `<katalog-exe>/ksefcli.yaml`                  |
|     5     | `~/.config/ksefcli/ksefcli.yaml` _(domyślne)_ |

```yaml
active_profile: firma1

profiles:
  firma1:
    environment: prod # test | demo | prod
    nip: "1234567890"
    token: "TWOJ_TOKEN_KSEF"

  firma2:
    environment: prod
    nip: "9876543210"
    certificate:
      private_key_file: ~/certyfikaty/klucz.pem
      certificate_file: ~/certyfikaty/cert.pem
      password_env: KSEF_CERT_PASSWORD
```

Token długoterminowy: portal KSeF → _Integracja → Tokeny_.

### 🖥 Uruchamianie GUI

```bash
./ksefcli                              # domyślnie uruchamia GUI
./ksefcli Gui -o ~/faktury --pdf       # z katalogiem wyjściowym i PDF
./ksefcli Gui --lan -o /data --pdf     # tryb LAN
```

| Opcja                | Opis                                 | Domyślnie |
| -------------------- | ------------------------------------ | :-------: |
| `-o`, `--outputdir`  | Katalog zapisu faktur                |    `.`    |
| `--pdf`              | Generuj PDF przy pobieraniu          | wyłączone |
| `--useInvoiceNumber` | Nazwa pliku wg numeru faktury        | wyłączone |
| `--lan`              | Nasłuchuj na wszystkich interfejsach | wyłączone |

### 🐳 Docker / serwer domowy

> ⚠️ Aplikacja **nie jest przeznaczona do wystawienia w internecie** — tylko sieć lokalna lub VPN.

```bash
cp .env.example .env
$EDITOR .env
docker compose up -d
```

**Architektura stosu:**

```text
Sieć lokalna (LAN)  :80 / :443
        │
   ┌────▼────┐   back   ┌──────────┐
   │ Traefik │ ◄──────► │ ksefcli  │
   │  proxy  │          │  :18150  │
   └─────────┘          └─────┬────┘
                              │
                        ┌─────▼────┐
                        │  Ofelia  │
                        │scheduler │
                        └──────────┘
```

| Serwis      | Obraz                                 | Rola                                                              |
| ----------- | ------------------------------------- | ----------------------------------------------------------------- |
| **Traefik** | `traefik:v3.6.7`                      | Reverse proxy — routing, TLS (DNS challenge), optional basic-auth |
| **ksefcli** | `ghcr.io/marcinbojko/ksef-gui:latest` | GUI — wyłącznie przez Traefik                                     |
| **Ofelia**  | `mcuadros/ofelia:latest`              | Harmonogram — rotacja logów, health-probe, czyszczenie faktur     |

<details>
<summary><b>Zmienne środowiskowe (.env)</b></summary>

| Zmienna                   | Opis                                                 | Domyślnie       |
| ------------------------- | ---------------------------------------------------- | --------------- |
| `TZ`                      | Strefa czasowa                                       | `Europe/Warsaw` |
| `TRAEFIK_TAG`             | Tag obrazu Traefik                                   | `v3.6.7`        |
| `KSEFCLI_TAG`             | Tag obrazu Docker                                    | `latest`        |
| `KSEFCLI_PORT`            | Port wewnętrzny                                      | `18150`         |
| `KSEFCLI_HOSTNAME`        | Hostname za Traefik                                  | —               |
| `KSEFCLI_BASICAUTH_USERS` | Hash basic-auth (`htpasswd -nb user pass`, `$`→`$$`) | wyłączone       |
| `OFELIA_TAG`              | Tag obrazu Ofelia                                    | `latest`        |

</details>

<details>
<summary><b>Woluminy</b></summary>

| Ścieżka               | Typ          | Opis                              |
| --------------------- | ------------ | --------------------------------- |
| `ksefcli-output`      | named volume | Pobrane faktury (`/data`)         |
| `ksefcli-config`      | named volume | `ksefcli.yaml`                    |
| `ksefcli-cache`       | named volume | Tokeny, preferencje, cache SQLite |
| `traefik-acme`        | named volume | Certyfikaty TLS Let's Encrypt     |
| `./ofelia/config.ini` | bind (ro)    | Harmonogram Ofelia                |

</details>

### 📄 Eksport PDF

PDF generowany **natywnie** przez [QuestPDF](https://www.questpdf.com/) — czysta implementacja .NET, bez zewnętrznych zależności.

```bash
./ksefcli XML2PDF faktura.xml                            # domyślny schemat (granatowy)
./ksefcli XML2PDF faktura.xml --color-scheme forest      # zielony
./ksefcli XML2PDF faktura.xml wynik.pdf --color-scheme slate  # szary
```

| Schemat             | Wygląd                         |
| ------------------- | ------------------------------ |
| `navy` _(domyślny)_ | Ciemny granat — klasyczny      |
| `forest`            | Ciemna zieleń — świeży akcent  |
| `slate`             | Ciemny szary — minimalistyczny |

---

## English

> **Fork** of [kamilcuk/ksefcli](https://github.com/kamilcuk/ksefcli) by [Kamil Cukrowski](https://github.com/kamilcuk). The original is a CLI tool; this fork adds a full browser-based GUI and additional features.

`ksefcli` is a tool for downloading invoices from Poland's **KSeF** (National e-Invoice System). It includes a built-in browser GUI that runs locally with no additional software required.

### ✨ Features

|                     |                                                                      |
| ------------------- | -------------------------------------------------------------------- |
| 🌐 **Browser GUI**  | Local interface, no installation needed                              |
| 📄 **PDF export**   | Native renderer (QuestPDF) — no Node.js, git, or external tools      |
| 🔄 **Auto-refresh** | Background search every N minutes; OS notifications for new invoices |
| 💾 **SQLite cache** | Search results stored locally; profile switching without re-fetching |
| 🌙 **Dark mode**    | Three independent modes: GUI, invoice preview, details panel         |
| 🐳 **Docker**       | Ready-to-use `docker-compose` with Traefik and Ofelia                |
| 🔒 **Offline**      | XSD validation and PDF generation work fully offline                 |

### 📸 Screenshots

<div align="center">

![Main screen](images/mainscreen.png)
_Main screen — invoice list_

![Preferences](images/prefs.png)
_Preferences panel_

![Configuration](images/config.png)
_Configuration editor_

</div>

### 📦 Installation

Download the latest binary from [Releases](https://github.com/marcinbojko/ksef-gui/releases).

<details>
<summary><b>🪟 Windows</b></summary>

Place `ksefcli-win-x64.exe` in any folder (rename to `ksefcli.exe` if you like).

Double-clicking the binary launches the GUI automatically.

</details>

<details>
<summary><b>🍎 macOS</b></summary>

```bash
# Apple Silicon
chmod +x ksefcli-osx-arm64
xattr -dr com.apple.quarantine ksefcli-osx-arm64

# Intel
chmod +x ksefcli-osx-x64
xattr -dr com.apple.quarantine ksefcli-osx-x64
```

> The quarantine attribute must be removed — otherwise macOS Gatekeeper blocks native library loading and PDF generation crashes.

</details>

<details>
<summary><b>🐧 Linux</b></summary>

```bash
chmod +x ksefcli-linux-x64   # or ksefcli-linux-arm64
```

</details>

### 🚀 Quick start

```bash
./ksefcli
# Browser opens automatically at http://localhost:<port>
```

On first launch without a config file the GUI opens the **setup wizard** automatically.

### ⚙ Configuration

`ksefcli` searches for `ksefcli.yaml` in this order:

| Priority | Location                                     |
| :------: | -------------------------------------------- |
|    1     | `-c /path/to/file` flag                      |
|    2     | `KSEFCLI_CONFIG` environment variable        |
|    3     | `./ksefcli.yaml` — current directory         |
|    4     | `<exe-dir>/ksefcli.yaml`                     |
|    5     | `~/.config/ksefcli/ksefcli.yaml` _(default)_ |

```yaml
active_profile: company1

profiles:
  company1:
    environment: prod # test | demo | prod
    nip: "1234567890"
    token: "YOUR_KSEF_TOKEN_HERE"

  company2:
    environment: prod
    nip: "9876543210"
    certificate:
      private_key_file: ~/certs/private.key
      certificate_file: ~/certs/cert.pem
      password_env: KSEF_CERT_PASSWORD
```

Obtain a long-term token from the KSeF portal: _Integracja → Tokeny_.

### 🖥 Running the GUI

```bash
./ksefcli                               # default — launches GUI
./ksefcli Gui -o ~/invoices --pdf       # with output directory and PDF
./ksefcli Gui --lan -o /data --pdf      # LAN mode
```

| Option               | Description                      | Default |
| -------------------- | -------------------------------- | :-----: |
| `-o`, `--outputdir`  | Directory for saving invoices    |   `.`   |
| `--pdf`              | Generate PDF when downloading    |   off   |
| `--useInvoiceNumber` | Use invoice number for filenames |   off   |
| `--lan`              | Listen on all network interfaces |   off   |

### 🐳 Docker / home server

> ⚠️ **Not intended for internet exposure.** Run on a local network (LAN) or behind a VPN only.

```bash
cp .env.example .env
$EDITOR .env
docker compose up -d
```

**Stack architecture:**

```text
Local network (LAN)  :80 / :443
        │
   ┌────▼────┐   back   ┌──────────┐
   │ Traefik │ ◄──────► │ ksefcli  │
   │  proxy  │          │  :18150  │
   └─────────┘          └─────┬────┘
                              │
                        ┌─────▼────┐
                        │  Ofelia  │
                        │scheduler │
                        └──────────┘
```

| Service     | Image                                 | Role                                                              |
| ----------- | ------------------------------------- | ----------------------------------------------------------------- |
| **Traefik** | `traefik:v3.6.7`                      | Reverse proxy — routing, TLS (DNS challenge), optional basic-auth |
| **ksefcli** | `ghcr.io/marcinbojko/ksef-gui:latest` | GUI — exposed exclusively via Traefik                             |
| **Ofelia**  | `mcuadros/ofelia:latest`              | Scheduler — log rotation, health probe, optional invoice cleanup  |

<details>
<summary><b>Environment variables (.env)</b></summary>

| Variable                  | Description                                          | Default         |
| ------------------------- | ---------------------------------------------------- | --------------- |
| `TZ`                      | Timezone                                             | `Europe/Warsaw` |
| `TRAEFIK_TAG`             | Traefik image tag                                    | `v3.6.7`        |
| `KSEFCLI_TAG`             | Docker image tag                                     | `latest`        |
| `KSEFCLI_PORT`            | Internal container port                              | `18150`         |
| `KSEFCLI_HOSTNAME`        | Hostname behind Traefik                              | —               |
| `KSEFCLI_BASICAUTH_USERS` | Basic-auth hash (`htpasswd -nb user pass`, `$`→`$$`) | disabled        |
| `OFELIA_TAG`              | Ofelia image tag                                     | `latest`        |

</details>

<details>
<summary><b>Volumes</b></summary>

| Path                  | Type         | Description                             |
| --------------------- | ------------ | --------------------------------------- |
| `ksefcli-output`      | named volume | Downloaded invoices (`/data`)           |
| `ksefcli-config`      | named volume | `ksefcli.yaml`                          |
| `ksefcli-cache`       | named volume | Session tokens, GUI prefs, SQLite cache |
| `traefik-acme`        | named volume | Let's Encrypt TLS certificates          |
| `./ofelia/config.ini` | bind (ro)    | Ofelia scheduler configuration          |

</details>

### 📄 PDF export

PDFs are rendered by a **native built-in engine** using [QuestPDF](https://www.questpdf.com/) — pure .NET, no external dependencies.

```bash
./ksefcli XML2PDF invoice.xml                              # default scheme (navy)
./ksefcli XML2PDF invoice.xml --color-scheme forest        # forest green
./ksefcli XML2PDF invoice.xml output.pdf --color-scheme slate  # slate grey
```

| Scheme             | Description                  |
| ------------------ | ---------------------------- |
| `navy` _(default)_ | Dark navy — classic, formal  |
| `forest`           | Dark green — fresh accent    |
| `slate`            | Dark grey — neutral, minimal |

---

<div align="center">

_Full CLI reference: [README.ksefcli.md](ksefcli/README.ksefcli.md) · License: [GPLv3](LICENSE.md)_

</div>
