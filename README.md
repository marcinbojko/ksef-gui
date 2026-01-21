# ksefcli

`ksefcli` to narzędzie wiersza poleceń (CLI) dla systemu Linux, napisane w języku C#, które ułatwia interakcję z Krajowym Systemem e-Faktur (KSeF) w Polsce. Aplikacja wykorzystuje bibliotekę kliencką `ksef-client-csharp` do komunikacji z usługami KSeF.

## Instalacja

Pobierz najnowszą wersję `ksefcli` z sekcji [Releases](https://github.com/your-repo/ksefcli/releases) i umieść ją w katalogu znajdującym się w `PATH`, na przykład `/usr/local/bin`.

## Konfiguracja

Przed rozpoczęciem pracy z `ksefcli`, należy skonfigurować aplikację, tworząc plik `ksefcli.yaml` w jednym z następujących miejsc:
- W katalogu bieżącym: `./ksefcli.yaml`
- W katalogu konfiguracyjnym użytkownika: `$HOME/.config/ksefcli/ksefcli.yaml`

Plik ten zawiera profile, które umożliwiają zarządzanie różnymi poświadczeniami i środowiskami KSeF.

### Struktura pliku `ksefcli.yaml`

```yaml
active_profile: <nazwa_aktywnego_profilu>
profiles:
  <nazwa_profilu_1>:
    environment: <srodowisko>
    nip: <nip_podmiotu>
    token: <token_autoryzacyjny>
    certificate:
      private_key: <sciezka_do_klucza_prywatnego>
      certificate: <sciezka_do_certyfikatu_publicznego>
      password_env: <zmienna_srodowiskowa_z_haslem>
  <nazwa_profilu_2>:
    # ...
```

### Opcje Konfiguracyjne

*   `active_profile`: (Opcjonalnie) Nazwa profilu, który będzie używany domyślnie, jeśli nie zostanie podany za pomocą opcji `--profile`. Jeśli zdefiniowany jest tylko jeden profil, `active_profile` jest ignorowane.
*   `profiles`: Mapa profili konfiguracyjnych.
    *   `<nazwa_profilu>`: Dowolna nazwa identyfikująca profil (np. `dyzio`, `firma_xyz_test`).
        *   `environment`: Środowisko KSeF (`test`, `demo`, `prod`).
        *   `nip`: Numer Identyfikacji Podatkowej (NIP) podmiotu, którego dotyczy profil.
        *   Należy zdefiniować **jedną** z poniższych metod uwierzytelniania:
            *   `token`: Token autoryzacyjny sesji.
            *   `certificate`: Dane certyfikatu kwalifikowanego.
                *   `private_key`: Ścieżka do klucza prywatnego (plik `.pem` lub `.pfx`). Można użyć `~` jako skrótu do katalogu domowego.
                *   `certificate`: Ścieżka do certyfikatu publicznego. Można użyć `~` jako skrótu do katalogu domowego.
                *   `password_env`: Nazwa zmiennej środowiskowej, która przechowuje hasło do klucza prywatnego.

### Przykład Konfiguracji

Poniższy przykład demonstruje konfigurację z wieloma profilami dla różnych podmiotów i środowisk.

```yaml
---
active_profile: dyzio
profiles:
  dyzio:
    environment: test
    nip: '5260215591'
    token: fdsafa
  dyzio2:
    environment: test
    nip: '5260215591'
    token: fdsfa
  kamcuk:
    environment: test
    nip: '5223217667'
    token: fdasfa
  cert_auth_example:
    environment: prod
    nip: '1234567890'
    certificate:
      private_key: '~/certs/my_private_key.pem'
      certificate: '~/certs/my_certificate.pem'
      password_env: 'KSEF_CERT_PASSWORD'

```

W tym przykładzie:
- Domyślnym profilem jest `dyzio`.
- Zdefiniowano trzy profile (`dyzio`, `dyzio2`, `kamcuk`) używające uwierzytelniania tokenem na środowisku testowym dla dwóch różnych NIP-ów.
- Profil `cert_auth_example` używa uwierzytelniania certyfikatem na środowisku produkcyjnym. Hasło do certyfikatu zostanie odczytane ze zmiennej środowiskowej `KSEF_CERT_PASSWORD`.

## Użycie

Ogólna składnia poleceń `ksefcli` jest następująca:

```bash
ksefcli <polecenie> [opcje]
```

### Opcje Globalne

Wszystkie polecenia akceptują następujące opcje globalne:

*   `-c`, `--config`: Ścieżka do pliku konfiguracyjnego `ksefcli.yaml`. Domyślnie: `$HOME/.config/ksefcli/ksefcli.yaml`.
*   `-a`, `--active`: Nazwa profilu do użycia, nadpisuje `active_profile` z pliku konfiguracyjnego.
*   `--cache`: Ścieżka do pliku cache'u tokenów. Domyślnie: `$HOME/.cache/ksefcli/ksefcli.json`.
*   `-v`, `--verbose`: Włącza szczegółowe logowanie (poziom DEBUG).
*   `-q`, `--quiet`: Włącza tryb cichy (wyświetla tylko ostrzeżenia i błędy).

### Dostępne Polecenia

#### `Auth`

Uwierzytelnia użytkownika na podstawie metody zdefiniowanej w aktywnym profilu (token lub certyfikat) i zwraca token dostępowy.

```bash
ksefcli -a moj_profil Auth
```

#### `TokenAuth`

Wymusza uwierzytelnienie za pomocą tokena sesyjnego, używając konfiguracji z aktywnego profilu. Profil musi zawierać klucz `token`.

```bash
ksefcli -a profil_z_tokenem TokenAuth
```

#### `CertAuth`

Wymusza uwierzytelnienie za pomocą certyfikatu kwalifikowanego, używając konfiguracji z aktywnego profilu. Profil musi zawierać sekcję `certificate`.

```bash
ksefcli -a profil_z_certyfikatem CertAuth
```

#### `TokenRefresh`

Odświeża token autoryzacyjny. *To polecenie jest w trakcie implementacji.*

#### `SzukajFaktur`

Wyszukuje faktury na podstawie podanych kryteriów. Odpowiada endpointowi `GET /online/Query/Invoice/Sync`.

*   `-s`, `--subject-type` (wymagane): Typ podmiotu (`Subject1`, `Subject2`, `Subject3`).
*   `--from` (wymagane): Data początkowa zakresu w formacie ISO-8601 (np. `2024-01-01T00:00:00Z`).
*   `--to` (wymagane): Data końcowa zakresu w formacie ISO-8601.
*   `--date-type`: Typ daty filtrowania (`invoicing`, `issue`, `payment`). Domyślnie: `issue`.
*   `--page-offset`: Numer strony wyników. Domyślnie: `0`.
*   `--page-size`: Liczba wyników na stronie. Domyślnie: `100`.

#### `ExportInvoices`

Inicjuje asynchroniczny eksport faktur.

*   `--from` (wymagane): Data początkowa w formacie ISO-8601.
*   `--to` (wymagane): Data końcowa w formacie ISO-8601.
*   `--date-type`: Typ daty (`invoicing`, `issue`, `payment`). Domyślnie: `issue`.
*   `-s`, `--subject-type` (wymagane): Typ podmiotu (`Subject1`, `Subject2`, `Subject3`).

#### `GetExportStatusCommand`

Sprawdza status zleconego eksportu faktur.

*   `--reference-number` (wymagane): Numer referencyjny eksportu, uzyskany z polecenia `ExportInvoices`.

#### `GetFaktura`

Pobiera pojedynczą fakturę w formacie XML.

*   `--ksef-id` (wymagane): Numer KSeF faktury.

## Cache Tokenów

`ksefcli` przechowuje tokeny autoryzacyjne w pamięci podręcznej, aby uniknąć konieczności wielokrotnego uwierzytelniania. Domyślna lokalizacja pliku z tokenami to `~/.cache/ksefcli/tokenstore.json`.

## Rozwój

Aby skonfigurować środowisko deweloperskie, wykonaj następujące kroki:

1.  Sklonuj repozytorium:
    ```bash
    git clone https://github.com/your-repo/ksefcli.git
    ```
2.  Zainstaluj zależności .NET:
    ```bash
    dotnet restore
    ```
3.  Zbuduj projekt:
    ```bash
    dotnet build
    ```
4.  Uruchom aplikację:
    ```bash
    dotnet run -- <polecenie> [opcje]
    ```

## Uwierzytelnianie w KSeF

Szczegółowe informacje na temat mechanizmów uwierzytelniania w Krajowym Systemie e-Faktur można znaleźć w oficjalnej dokumentacji: [Uwierzytelnianie w KSeF](https://github.com/CIRFMF/ksef-docs/blob/main/uwierzytelnianie.md).
