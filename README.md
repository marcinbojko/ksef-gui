# ksefcli — GUI

**PL** | [EN](#english)

---

## Polski

> **Fork** — ten projekt jest forkiem [kamilcuk/ksefcli](https://github.com/kamilcuk/ksefcli) autorstwa [Kamila Cukrowskiego](https://github.com/kamilcuk). Oryginalne repozytorium zawiera wersję CLI; ten fork dodaje rozbudowany interfejs przeglądarkowy (GUI) oraz dodatkowe funkcje.

`ksefcli` to narzędzie do pobierania faktur z **Krajowego Systemu e-Faktur (KSeF)**. Oprócz interfejsu wiersza poleceń posiada wbudowany interfejs przeglądarkowy (GUI), który uruchamia się lokalnie i nie wymaga instalacji dodatkowego oprogramowania.

### Wymagania

- Plik wykonywalny `ksefcli` (Linux / Windows / macOS) — samowystarczalny, brak zależności .NET
- Przeglądarka internetowa

Eksport PDF **nie wymaga** Node.js, git ani żadnych zewnętrznych narzędzi — generator PDF jest wbudowany w aplikację.

### Instalacja

Pobierz najnowszy plik binarny dla swojej platformy ze strony [Releases](https://github.com/marcinbojko/ksef-gui/releases).

#### Windows

Umieść `ksefcli-win-x64.exe` w wybranym folderze (możesz zmienić nazwę na `ksefcli.exe`).

#### macOS

Umieść `ksefcli-osx-arm64` (Apple Silicon) lub `ksefcli-osx-x64` (Intel) w wybranym miejscu i nadaj uprawnienia do wykonania:

```bash
chmod +x ksefcli-osx-arm64
```

Plik pobrany z internetu jest domyślnie objęty kwarantanną macOS (Gatekeeper), co blokuje ładowanie natywnych bibliotek i powoduje błąd przy generowaniu PDF. Usuń atrybut kwarantanny przed pierwszym uruchomieniem:

```bash
xattr -dr com.apple.quarantine ksefcli-osx-arm64
```

#### Linux

Umieść `ksefcli-linux-x64` w wybranym miejscu i nadaj uprawnienia do wykonania:

```bash
chmod +x ksefcli-linux-x64
```

---

### Szybki start

```bash
./ksefcli
# Przeglądarka otwiera się automatycznie pod adresem http://localhost:<port>
```

Polecenie `Gui` jest domyślne — samo uruchomienie pliku wykonywalnego (np. dwuklikiem w systemie Windows) otwiera GUI.

Przy pierwszym uruchomieniu bez pliku konfiguracyjnego — GUI otwiera **kreator konfiguracji** automatycznie.

### Plik konfiguracyjny

`ksefcli` szuka pliku `ksefcli.yaml` w następującej kolejności:

| Priorytet | Lokalizacja |
|-----------|-------------|
| 1 | Flaga `-c /sciezka/do/pliku` |
| 2 | Zmienna środowiskowa `KSEFCLI_CONFIG` |
| 3 | `./ksefcli.yaml` — bieżący katalog roboczy |
| 4 | `<katalog-exe>/ksefcli.yaml` — katalog obok pliku wykonywalnego |
| 5 | `~/.config/ksefcli/ksefcli.yaml` — domyślna lokalizacja |

Najwygodniejsze podejście: umieść `ksefcli.yaml` obok pliku wykonywalnego — działa z dowolnego miejsca.

Na starcie aplikacja wypisuje, który plik został wczytany:
```
Config: /home/user/.config/ksefcli/ksefcli.yaml [default (~/.config/ksefcli/)]
```

#### Format pliku konfiguracyjnego

```yaml
active_profile: firma1

profiles:
  firma1:
    environment: prod      # test | demo | prod
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

Jeśli zdefiniowany jest tylko jeden profil, `active_profile` jest opcjonalne.

Token długoterminowy uzyskasz w portalu KSeF: *Integracja → Tokeny*.

### Uruchamianie GUI

```bash
# Podstawowe uruchomienie (Gui jest domyślne)
./ksefcli

# Z katalogiem wyjściowym i eksportem PDF
./ksefcli Gui -o ~/faktury --pdf

# Tryb LAN — dostęp z innych urządzeń w sieci
./ksefcli Gui --lan -o /data --pdf
```

| Opcja | Opis | Domyślnie |
|-------|------|-----------|
| `-o`, `--outputdir` | Katalog zapisu faktur | `.` |
| `-p`, `--pdf` | Generuj pliki PDF przy pobieraniu | wyłączone |
| `--useInvoiceNumber` | Używaj numeru faktury zamiast numeru KSeF w nazwie pliku | wyłączone |
| `--lan` | Nasłuchuj na wszystkich interfejsach sieciowych | wyłączone |

### Funkcje GUI

![Główny ekran](images/mainscreen.png)

**Wyszukiwanie faktur**
- Typ podmiotu: Sprzedawca / Nabywca / Subject3 / Authorized
- Zakres dat (wybieracz miesięcy), typ daty: Wystawienie / Sprzedaż / PermanentStorage
- Filtrowanie po walucie — przyciski walut budowane dynamicznie na podstawie wyników wyszukiwania
- Limit wyświetlanych wierszy (5 / 10 / 50 / 100, domyślnie 50) z przyciskiem *Pokaż wszystkie*

**Tabela wyników**
- Numer KSeF, numer faktury, data wystawienia, sprzedawca, nabywca, kwota brutto, waluta
- Wskaźniki statusu pliku — które faktury są już pobrane jako XML / PDF / JSON
- Podgląd szczegółów faktury po kliknięciu lupki (strony, pozycje, podsumowanie)

**Pobieranie**
- Zaznaczanie pojedynczych faktur lub wszystkich
- Wybór katalogu wyjściowego (przeglądarka folderów)
- Formaty eksportu: XML (domyślnie włączony), PDF (włączony przy `--pdf`), JSON
- Własny schemat nazw: `YYYY-MM-DD-Sprzedawca-Waluta-NumerKSeF`
- "Separuj po NIP" — tworzy podkatalog według NIP aktywnego profilu

**Status tokenu**
- Wyświetla czas ważności tokenu dostępu i tokenu odświeżania
- Kolorowy przycisk *Autoryzuj* (zielony / pomarańczowy / czerwony)
- Automatyczne odświeżenie tokenu dostępu przy starcie aplikacji — jeśli token dostępu wygasł, ale token odświeżania jest ważny, aplikacja odnawia sesję bez interakcji użytkownika
- Ponowna autoryzacja bez restartu

**⚙ Preferencje** (panel z zakładkami)

Zakładka **Ogólne**:
- Katalog wyjściowy, formaty eksportu (XML / PDF / JSON), schemat nazw plików
- Separacja po NIP (podkatalog = NIP aktywnego profilu)
- Limit wyświetlanych faktur w tabeli (5 / 10 / 50 / 100)
- Wybór aktywnego profilu (zapamiętywany między sesjami; zmiana profilu działa natychmiast bez restartu, lista faktur ładowana z pamięci podręcznej)
- **Auto-odświeżanie** — cykliczne wyszukiwanie co N minut (0 = wyłączone):
  - Aktywny profil: automatyczne odświeżanie w tle obsługiwane przez przeglądarkę
  - Pozostałe profile oznaczone jako *Uwzględnij w auto-odświeżaniu* (patrz edytor konfiguracji): przeszukiwane w tle przez serwer C#, wyniki zapisywane do bazy danych; powiadomienie (systemowe lub badge🔔 w liście profili), gdy pojawią się nowe faktury

Zakładka **Eksport**:
- Szczegółowe opcje eksportu plików

Zakładka **Sieć**:
- Port nasłuchiwania (domyślnie `18150`) — zmiana wymaga restartu
- Tryb nasłuchiwania: **Tylko localhost** (domyślnie) lub **Sieć lokalna (0.0.0.0)**
- Wyświetla aktualny adres URL serwera

Zakładka **Wygląd**:
- Trzy niezależne tryby ciemne: interfejs GUI, podgląd faktury (HTML), szczegóły faktury
- Schemat kolorów PDF: Granatowy / Zielony / Szary
- Przycisk *Wyślij testowe powiadomienie* — weryfikacja uprawnień do powiadomień przeglądarki
- Przełącznik formatu logów konsoli: czytelny dla człowieka (domyślnie) lub JSON (dla CI/systemd)

Preferencje zapisywane są w: `~/.cache/ksefcli/gui-prefs.json`

![Preferencje](images/prefs.png)

**✎ Konfiguracja** (edytor w przeglądarce)
- Edycja profili: nazwa, NIP, środowisko, metoda uwierzytelnienia
- Pole tokenu z przełącznikiem widoczności
- Pola certyfikatu (plik klucza, plik certyfikatu, hasło/env/plik)
- **Uwzględnij w auto-odświeżaniu** — checkbox per profil; zaznaczone profile są przeszukiwane przez serwer w tle (domyślnie włączone dla wszystkich profili); wyniki są buforowane w SQLite (`~/.cache/ksefcli/db/invoice-cache.db`)
- Dodawanie i usuwanie profili
- Zmiany zapisywane natychmiast do `ksefcli.yaml`; lista profili odświeżana bez restartu

![Konfiguracja](images/config.png)

### Pamięć podręczna faktur

Wyniki wyszukiwania są zapisywane lokalnie w bazie SQLite:

```text
~/.cache/ksefcli/db/invoice-cache.db
```

- Jedna linia na profil (klucz = SHA-256 konfiguracji profilu), zawsze nadpisywana
- Przy przełączeniu profilu lista faktur jest natychmiast wczytywana z bazy — bez konieczności ponownego wyszukiwania
- Wyszukiwanie ręczne nadpisuje buforowane parametry; auto-odświeżanie (tło) aktualizuje tylko listę faktur, nie zmieniając parametrów ostatniego wyszukiwania ręcznego

### Kreator pierwszego uruchomienia

Jeśli plik `ksefcli.yaml` nie istnieje:
1. GUI tworzy plik szablonowy w domyślnej lokalizacji
2. Pojawia się baner ostrzegawczy *"Brak konfiguracji"*
3. Przyciski wyszukiwania, pobierania i autoryzacji są zablokowane
4. Edytor konfiguracji otwiera się automatycznie

Po zapisaniu profilu — wszystkie przyciski odblokowują się bez restartu.

### Docker / serwer domowy

Dla uruchomienia na serwerze domowym lub NAS w sieci lokalnej. Compose dostarcza Traefik jako reverse proxy oraz Ofelia jako harmonogram zadań.

> **Aplikacja nie jest przeznaczona do wystawienia w internecie.** Powinna działać wyłącznie w sieci lokalnej (LAN) lub przez VPN.

#### Szybki start

```bash
# 1. Skopiuj plik zmiennych środowiskowych i uzupełnij wartości
cp .env.example .env
$EDITOR .env

# 2. Uruchom stos
docker compose up -d
```

#### Architektura stosu

```
Sieć lokalna (LAN)
   │  :80
   │  :443 (opcjonalne TLS)
   ▼
┌─────────┐   sieć back    ┌──────────┐
│ Traefik │ ◄────────────► │ ksefcli  │
│  proxy  │                │ :18150   │
└─────────┘                └──────────┘
                                 │
                           sieć back (internal)
                                 │
                            ┌─────────┐
                            │ Ofelia  │
                            │scheduler│
                            └─────────┘
```

| Serwis | Obraz | Rola |
|--------|-------|------|
| **Traefik** | `traefik:v3.6.7` | Reverse proxy w sieci lokalnej — routing, opcjonalne TLS, opcjonalne basic-auth |
| **ksefcli** | `ghcr.io/marcinbojko/ksef-gui:latest` | GUI nasłuchuje na porcie `18150`, wystawione wyłącznie przez Traefik |
| **Ofelia** | `mcuadros/ofelia:latest` | Harmonogram zadań — rotacja logów, health-probe, opcjonalne czyszczenie starych faktur |

#### Traefik — konfiguracja

Traefik jest konfigurowany przez plik statyczny `traefik/traefik.yml` (montowany do kontenera jako `/etc/traefik/traefik.yml`):

| Funkcja | Konfiguracja |
|---------|-------------|
| HTTP→HTTPS redirect | EntryPoint `http` z trwałym przekierowaniem na `https` |
| Certyfikaty TLS | **DNS Challenge** — nie wymaga publicznego portu 443; działa w sieci lokalnej |
| Provider DNS | Domyślnie Cloudflare; zmień w `traefik/traefik.yml` (`dnsChallenge.provider`) |
| Routing | Docker provider — trasy definiowane przez labels na kontenerze |
| IP allowlist | Middleware `local-only@file` — dostęp tylko z prywatnych zakresów IP |
| HSTS | Middleware `hsts-header@file` — nagłówek `Strict-Transport-Security` |
| Basic-auth | *(opcjonalne)* Dodaj `,ksefcli-auth@docker` do middlewares w labels kontenera |
| Dashboard | Wyłączony (`dashboard: false`) |

**Konfiguracja TLS/ACME** (`traefik/traefik.yml`):

```yaml
certificatesResolvers:
  letsencrypt:
    acme:
      email: changeme@example.com   # ← ustaw swój e-mail
      dnsChallenge:
        provider: cloudflare        # ← zmień na swojego dostawcę DNS
```

**Poświadczenia dostawcy DNS** (`traefik/dns-provider.env`):

```bash
cp traefik/dns-provider.env.example traefik/dns-provider.env
$EDITOR traefik/dns-provider.env   # wpisz token API Cloudflare lub innego dostawcy
```

**Generowanie hasła basic-auth** (opcjonalne, zainstaluj `apache2-utils`):

```bash
htpasswd -nb admin secretpassword
# Wynik: admin:$apr1$xyz...
# W pliku .env znaki $ muszą być podwojone: admin:$$apr1$$xyz...
KSEFCLI_BASICAUTH_USERS=admin:$$apr1$$xyz...
```

#### Sieć

Compose definiuje dwie wewnętrzne sieci — nie wymagają wcześniejszego tworzenia ani zewnętrznych zasobów:

| Sieć | Typ | Połączone serwisy | Cel |
|------|-----|-------------------|-----|
| `front` | bridge | Traefik | Porty 80/443 wystawione na hoście — ruch zewnętrzny do Traefik |
| `back` | bridge | Traefik, ksefcli, Ofelia | Komunikacja wewnętrzna: Traefik↔ksefcli oraz Ofelia↔ksefcli |

#### Zmienne środowiskowe (`.env`)

Skopiuj `.env.example` i dostosuj:

| Zmienna | Opis | Domyślnie |
|---------|------|-----------|
| `TZ` | Strefa czasowa | `Europe/Warsaw` |
| `TRAEFIK_TAG` | Tag obrazu Traefik | `v3.6.7` |
| `KSEFCLI_TAG` | Tag obrazu Docker | `latest` |
| `KSEFCLI_PORT` | Port wewnętrzny kontenera | `18150` |
| `KSEFCLI_HOSTNAME` | Hostname za Traefik (np. `ksef.nas.local`) | — |
| `KSEFCLI_BASICAUTH_USERS` | *(opcjonalne)* Hash basic-auth — wygeneruj przez `htpasswd -nb user pass`, `$` → `$$` | wyłączone |
| `OFELIA_TAG` | Tag obrazu Ofelia | `latest` |

#### Ofelia — zadania cykliczne (`ofelia/config.ini`)

Ofelia wykonuje zadania bezpośrednio w kontenerze `ksefcli` (`job-exec`) lub przez Docker API (`job-run`):

| Zadanie | Typ | Harmonogram | Opis |
|---------|-----|-------------|------|
| `log-rotate` | `job-exec` | `@daily` | Usuwa pliki logów Serilog (`ksefcli-*.log`) starsze niż 7 dni |
| `health-check` | `job-run` | `@every 5m` | Sprawdza status healthcheck; restartuje kontener gdy nie jest `healthy` |
| `cleanup-old-invoices` | `job-exec` | `@weekly` *(wyłączone)* | Usuwa pliki `.xml`/`.pdf`/`.json` starsze niż 365 dni — odkomentuj i dostosuj |

Edytuj `ofelia/config.ini` żeby zmienić harmonogramy lub włączyć czyszczenie faktur. Zmiany wymagają `docker compose restart ofelia`.

#### Woluminy i pliki hosta

| Ścieżka | Typ | Opis |
|---------|-----|------|
| `ksefcli-output` | named volume | Pobrane faktury — trwałe między restartami; domyślny katalog wyjściowy `/data` |
| `ksefcli-config` | named volume | Konfiguracja ksefcli (`ksefcli.yaml`) — tworzona automatycznie przez aplikację |
| `ksefcli-cache` | named volume | Tokeny sesji, preferencje GUI i baza faktur SQLite — przeżywają `docker compose down/up` |
| `traefik-acme` | named volume | Certyfikaty TLS Let's Encrypt — zachowane między restartami |
| `./ofelia/config.ini` | bind (ro) | Konfiguracja harmonogramu zadań Ofelia |

### Eksport PDF

PDF jest generowany **natywnie przez wbudowany renderer** oparty na [QuestPDF](https://www.questpdf.com/) — czysta implementacja .NET, bez zewnętrznych zależności.

Nie jest wymagany Node.js, git ani żaden zewnętrzny generator. Eksport PDF działa identycznie na każdej platformie i w środowisku Docker.

#### Schemat kolorów

Wygląd nagłówków tabel i akcentów w PDF można zmienić w preferencjach GUI (zakładka ⚙):

| Schemat | Opis |
|---------|------|
| **Granatowy** (domyślny) | Ciemny niebieski — klasyczny, formalny wygląd |
| **Zielony** | Ciemna zieleń — świeży, ekologiczny akcent |
| **Szary** | Ciemny szary — neutralny, minimalistyczny |

Schemat dotyczy nagłówków tabel, obramowań sekcji i koloru akcentowego. Tło dokumentu zawsze białe, tekst czarny.

Konwersja z wiersza poleceń:

```bash
# Domyślny schemat (granatowy)
./ksefcli XML2PDF faktura.xml

# Wskazanie schematu
./ksefcli XML2PDF faktura.xml --color-scheme forest
./ksefcli XML2PDF faktura.xml wynik.pdf --color-scheme slate
```

---

## English

> **Fork** — this project is a fork of [kamilcuk/ksefcli](https://github.com/kamilcuk/ksefcli) by [Kamil Cukrowski](https://github.com/kamilcuk). The original repository provides a CLI tool; this fork adds a full browser-based GUI and additional features.

`ksefcli` is a tool for downloading invoices from Poland's **KSeF** (National e-Invoice System). In addition to its command-line interface it includes a built-in browser-based GUI that runs locally with no additional software required.

### Requirements

- `ksefcli` binary (Linux / Windows / macOS) — self-contained, no .NET runtime needed
- A web browser

PDF export **does not require** Node.js, git, or any external tools — the PDF renderer is built into the application.

### Installation

Download the latest binary for your platform from the [Releases](https://github.com/marcinbojko/ksef-gui/releases) page.

#### Windows

Place `ksefcli-win-x64.exe` in any folder (rename to `ksefcli.exe` if desired).

#### macOS

Place `ksefcli-osx-arm64` (Apple Silicon) or `ksefcli-osx-x64` (Intel) anywhere and make it executable:

```bash
chmod +x ksefcli-osx-arm64
```

Files downloaded from the internet are quarantined by macOS Gatekeeper, which prevents native libraries from loading and causes a crash on PDF generation. Clear the quarantine attribute before first run:

```bash
xattr -dr com.apple.quarantine ksefcli-osx-arm64
```

#### Linux

Place `ksefcli-linux-x64` anywhere and make it executable:

```bash
chmod +x ksefcli-linux-x64
```

---

### Quick start

```bash
./ksefcli
# Browser opens automatically at http://localhost:<port>
```

`Gui` is the default command — double-clicking the binary (e.g. on Windows) opens the GUI directly.

On first launch without a config file the GUI opens the **setup wizard** automatically.

### Configuration file

`ksefcli` searches for `ksefcli.yaml` in this order:

| Priority | Location |
|----------|----------|
| 1 | `-c /path/to/file` flag |
| 2 | `KSEFCLI_CONFIG` environment variable |
| 3 | `./ksefcli.yaml` — current working directory |
| 4 | `<exe-dir>/ksefcli.yaml` — same directory as the binary |
| 5 | `~/.config/ksefcli/ksefcli.yaml` — default fallback |

The most convenient setup: place `ksefcli.yaml` next to the binary — works from any directory.

On startup, ksefcli prints which file was loaded:
```
Config: /home/user/.config/ksefcli/ksefcli.yaml [default (~/.config/ksefcli/)]
```

#### Config file format

```yaml
active_profile: company1

profiles:
  company1:
    environment: prod      # test | demo | prod
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

If only one profile is defined, `active_profile` is optional.

Obtain a long-term token from the KSeF portal under *Integracja → Tokeny*.

### Running the GUI

```bash
# Basic (Gui is the default command)
./ksefcli

# With output directory and PDF export
./ksefcli Gui -o ~/invoices --pdf

# LAN mode — accessible from other devices on the network
./ksefcli Gui --lan -o /data --pdf
```

| Option | Description | Default |
|--------|-------------|---------|
| `-o`, `--outputdir` | Directory for saving invoices | `.` |
| `-p`, `--pdf` | Generate PDF files when downloading | off |
| `--useInvoiceNumber` | Use invoice number instead of KSeF number for filenames | off |
| `--lan` | Listen on all network interfaces | off |

### GUI features

![Main screen](images/mainscreen.png)

**Invoice search**
- Subject type: Seller / Buyer / Subject3 / Authorized
- Date range (month picker), date type: Issue / Invoicing / PermanentStorage
- Per-currency filter chips — built dynamically from the current search results
- Display row limit (5 / 10 / 50 / 100, default 50) with a *Show all* button

**Results table**
- KSeF number, invoice number, issue date, seller, buyer, gross amount, currency
- File status indicators — which invoices are already downloaded as XML / PDF / JSON
- Click the magnifying glass to preview invoice details (parties, line items, totals)

**Download**
- Select individual invoices or all at once
- Folder picker for output directory
- Export formats: XML (default on), PDF (on with `--pdf`), JSON (default off)
- Custom filename pattern: `YYYY-MM-DD-SellerName-Currency-KsefNumber`
- "Separate by NIP" — creates a subdirectory named after the active profile's NIP

**Token status**
- Displays access token and refresh token expiry times
- Colour-coded Autoryzuj button (green / orange / red)
- **Automatic token refresh on startup** — if the access token is expired but the refresh token is still valid, the session is silently renewed without any user action
- Re-authenticate manually without restarting

**⚙ Preferences** (tabbed panel)

**General** tab:
- Output directory, export formats (XML / PDF / JSON), filename style
- Separate-by-NIP option (subdirectory = active profile's NIP)
- Display row limit (5 / 10 / 50 / 100)
- Active profile selection (persisted across sessions; switching takes effect immediately, invoice list loaded from cache)
- **Auto-refresh** — background search every N minutes (0 = disabled):
  - Active profile: browser-driven background refresh with live table updates
  - Other profiles marked *Include in auto-refresh* (see config editor): searched in the background by the server, results written to SQLite; OS notification + 🔔 badge on the profile dropdown when new invoices arrive

**Export** tab:
- Detailed file export options

**Network** tab:
- Listening port (default `18150`) — change takes effect on next restart
- Listen mode: **Localhost only** (default) or **All interfaces (0.0.0.0)**
- Displays the current server URL

**Appearance** tab:
- Three independent dark modes: GUI interface, invoice HTML preview, invoice details panel
- PDF colour scheme: Navy / Forest / Slate
- *Send test notification* button — verify browser notification permissions
- Console log format toggle: human-readable (default) or JSON (for CI/systemd)

Preferences stored at: `~/.cache/ksefcli/gui-prefs.json`

![Preferences](images/prefs.png)

**✎ Config editor** (in-browser)
- Edit profiles: name, NIP, environment, auth method
- Token field with show/hide toggle
- Certificate fields (key file, cert file, password / env var / file)
- **Include in auto-refresh** checkbox per profile — enabled by default for all profiles; the server searches checked profiles in the background when auto-refresh is active, caching results in SQLite (`~/.cache/ksefcli/db/invoice-cache.db`)
- Add and delete profiles
- Saves immediately to `ksefcli.yaml`; profile dropdown refreshes without restart

![Configuration](images/config.png)

### Invoice cache

Search results are persisted locally in a SQLite database:

```text
~/.cache/ksefcli/db/invoice-cache.db
```

- One row per profile (key = SHA-256 of the profile config), always overwritten on new search
- Switching profiles immediately loads the cached invoice list — no re-search needed
- A manual search overwrites both the invoice list and the search parameters; a background auto-refresh updates only the invoice list, preserving the user's last explicit search parameters

### First-run wizard

If `ksefcli.yaml` does not exist at startup:
1. GUI creates a template config at the default path
2. An amber warning banner appears: *"Brak konfiguracji"*
3. Search, download, and auth buttons are disabled
4. Config editor opens automatically

After saving a profile, all buttons re-enable — no restart needed.

### Docker / home server

For running on a home server or NAS on a local network. The compose file includes Traefik as a reverse proxy and Ofelia as a job scheduler.

> **This app is not intended to be exposed to the internet.** It should run on a local network (LAN) or behind a VPN only.

#### Quick start

```bash
# 1. Copy the environment file and fill in your values
cp .env.example .env
$EDITOR .env

# 2. Bring the stack up
docker compose up -d
```

#### Stack architecture

```
Local network (LAN)
   │  :80
   │  :443 (optional TLS)
   ▼
┌─────────┐   back network   ┌──────────┐
│ Traefik │ ◄──────────────► │ ksefcli  │
│  proxy  │                  │ :18150   │
└─────────┘                  └──────────┘
                                   │
                           back network
                                   │
                              ┌─────────┐
                              │ Ofelia  │
                              │scheduler│
                              └─────────┘
```

| Service | Image | Role |
|---------|-------|------|
| **Traefik** | `traefik:v3.6.7` | Local reverse proxy — routing, optional TLS, optional basic-auth |
| **ksefcli** | `ghcr.io/marcinbojko/ksef-gui:latest` | GUI listening on port `18150`, exposed exclusively through Traefik |
| **Ofelia** | `mcuadros/ofelia:latest` | Job scheduler — log rotation, health probe, optional old-invoice cleanup |

#### Traefik — configuration

Traefik is configured via the static file `traefik/traefik.yml` (mounted into the container at `/etc/traefik/traefik.yml`):

| Feature | Configuration |
|---------|--------------|
| HTTP→HTTPS redirect | `http` entrypoint with permanent redirect to `https` |
| TLS certificates | **DNS Challenge** — does not require public port 443; works on a local network |
| DNS provider | Cloudflare by default; change in `traefik/traefik.yml` (`dnsChallenge.provider`) |
| Routing | Docker provider — routes defined by labels on the ksefcli container |
| IP allowlist | `local-only@file` middleware — access restricted to private IP ranges |
| HSTS | `hsts-header@file` middleware — `Strict-Transport-Security` header |
| Basic-auth | *(optional)* Append `,ksefcli-auth@docker` to the container's middlewares label |
| Dashboard | Disabled (`dashboard: false`) |

**TLS/ACME configuration** (`traefik/traefik.yml`):

```yaml
certificatesResolvers:
  letsencrypt:
    acme:
      email: changeme@example.com   # ← set your email
      dnsChallenge:
        provider: cloudflare        # ← change to your DNS provider if needed
```

**DNS provider credentials** (`traefik/dns-provider.env`):

```bash
cp traefik/dns-provider.env.example traefik/dns-provider.env
$EDITOR traefik/dns-provider.env   # fill in your Cloudflare API token or other provider credentials
```

**Generating a basic-auth password** (optional, requires `apache2-utils`):

```bash
htpasswd -nb admin secretpassword
# Output: admin:$apr1$xyz...
# In .env, dollar signs must be doubled: admin:$$apr1$$xyz...
KSEFCLI_BASICAUTH_USERS=admin:$$apr1$$xyz...
```

#### Networks

Two networks defined by compose — no external resources or pre-creation required:

| Network | Type | Connected services | Purpose |
|---------|------|--------------------|---------|
| `front` | bridge | Traefik | Host ports 80/443 — external traffic into Traefik |
| `back` | bridge | Traefik, ksefcli, Ofelia | Internal traffic: Traefik→ksefcli and Ofelia→ksefcli |

#### Environment variables (`.env`)

Copy `.env.example` and adjust:

| Variable | Description | Default |
|----------|-------------|---------|
| `TZ` | Timezone | `Europe/Warsaw` |
| `TRAEFIK_TAG` | Traefik image tag | `v3.6.7` |
| `KSEFCLI_TAG` | Docker image tag | `latest` |
| `KSEFCLI_PORT` | Internal container port | `18150` |
| `KSEFCLI_HOSTNAME` | Hostname behind Traefik (e.g. `ksef.nas.local`) | — |
| `KSEFCLI_BASICAUTH_USERS` | *(optional)* Basic-auth hash — generate with `htpasswd -nb user pass`, escape `$` → `$$` | disabled |
| `OFELIA_TAG` | Ofelia image tag | `latest` |

#### Ofelia scheduled jobs (`ofelia/config.ini`)

Ofelia runs tasks directly inside the `ksefcli` container (`job-exec`) or via the Docker API (`job-run`):

| Job | Type | Schedule | Description |
|-----|------|----------|-------------|
| `log-rotate` | `job-exec` | `@daily` | Deletes Serilog rolling log files (`ksefcli-*.log`) older than 7 days |
| `health-check` | `job-run` | `@every 5m` | Checks healthcheck status; restarts the container if not `healthy` |
| `cleanup-old-invoices` | `job-exec` | `@weekly` *(disabled)* | Deletes `.xml`/`.pdf`/`.json` files older than 365 days — uncomment and adjust |

Edit `ofelia/config.ini` to change schedules or enable invoice cleanup. Changes require `docker compose restart ofelia`.

#### Volumes and host files

| Path | Type | Description |
|------|------|-------------|
| `ksefcli-output` | named volume | Downloaded invoices — persisted across restarts; default output path `/data` |
| `ksefcli-config` | named volume | ksefcli configuration (`ksefcli.yaml`) — created automatically by the app |
| `ksefcli-cache` | named volume | Session tokens, GUI preferences, and invoice SQLite cache — survive `docker compose down/up` |
| `traefik-acme` | named volume | Let's Encrypt TLS certificates — preserved across restarts |
| `./ofelia/config.ini` | bind (ro) | Ofelia job scheduler configuration |

### PDF export

PDFs are rendered by a **native built-in engine** based on [QuestPDF](https://www.questpdf.com/) — a pure .NET library with no external dependencies.

Node.js, git, and any external generator are not required. PDF export works identically on all platforms and inside Docker with no additional setup.

#### Colour schemes

The look of table headers and accents can be changed in the GUI preferences (⚙ tab):

| Scheme | Description |
|--------|-------------|
| **Navy** (default) | Dark navy blue — classic, formal look |
| **Forest** | Dark green — fresh accent |
| **Slate** | Dark grey — neutral, minimalist |

The scheme affects table header backgrounds, section border colours, and the brand accent. Document background is always white; body text is always black.

Command-line conversion:

```bash
./ksefcli XML2PDF invoice.xml
./ksefcli XML2PDF invoice.xml output.pdf --color-scheme forest
./ksefcli XML2PDF invoice.xml output.pdf --color-scheme slate
```

---

*Full CLI reference: [README.ksefcli.md](README.ksefcli.md)*
*License: [GPLv3](LICENSE.md)*
