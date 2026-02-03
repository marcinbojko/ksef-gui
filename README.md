# ksefcli

`ksefcli` to narzędzie wiersza poleceń (CLI) dla systemu Linux, napisane w języku C#, które ułatwia interakcję z Krajowym Systemem e-Faktur (KSeF) w Polsce. Aplikacja wykorzystuje bibliotekę kliencką `ksef-client-csharp` do komunikacji z usługami KSeF.

## Spis Treści

- [Instalacja](#instalacja)
- [Przykłady użycia](#przykłady-użycia)
- [Konfiguracja](#konfiguracja)
  - [Struktura pliku `ksefcli.yaml`](#struktura-pliku-ksefcliyaml)
  - [Opcje Konfiguracyjne](#opcje-konfiguracyjne)
  - [Przykład Konfiguracji](#przykład-konfiguracji)
- [Użycie](#użycie)
  - [Opcje Globalne](#opcje-globalne)
  - [Dostępne Polecenia](#dostępne-polecenia)
- [Polecenia](#polecenia)
  - [`Auth`](#auth)
  - [`TokenAuth`](#tokenauth)
  - [`CertAuth`](#certauth)
  - [`TokenRefresh`](#tokenrefresh)
  - [`GetFaktura`](#getfaktura)
  - [`SzukajFaktur`](#szukajfaktur)
  - [`PobierzFaktury`](#pobierzfaktury)
  - [`PrzeslijFaktury`](#przeslijfaktury)
  - [`LinkDoFaktury`](#linkdofaktury)
  - [`QRDoFaktury`](#qrdofaktury)
  - [`XML2PDF`](#xml2pdf)
- [Rozwój](#rozwój)
- [Uwierzytelnianie w KSeF](#uwierzytelnianie-w-ksef)
- [Autor i Licencja](#autor-i-licencja)

## Instalacja

Możesz pobrać statycznie linkowaną binarkę `ksefcli` bezpośrednio z artefaktów GitLab CI/CD, a następnie umieścić ją w katalogu znajdującym się w `PATH` (np. `/usr/local/bin`).

Poniższy link jest przeznaczony dla systemu Linux.

```bash
curl -LsS https://gitlab.com/kamcuk/ksefcli/builds/artifacts/main/download?job=linux_build-main | zcat > ksefcli
chmod +x ksefcli
sudo mv ksefcli /usr/local/bin/
```

### Bezpośrednie linki do pobrania

- [Linux x64](https://gitlab.com/kamcuk/ksefcli/-/jobs/artifacts/main/raw/ksefcli?job=linux_build-main)
- [Windows x64](https://gitlab.com/kamcuk/ksefcli/-/jobs/artifacts/main/raw/ksefcli.exe?job=windows_build-main)


## Przykłady użycia

Wyszukiwanie numeru KSeF dla faktury o konkretnym numerze:
```bash
$ ksefcli SzukajFaktur -q -c ksefcli.yaml --from "-1week" --to "now" --invoiceNumber '0004/26' | jq -r '.Invoices[0].KsefNumber'
12312312312-20260117-XXXXXXXXXXXX-5C
```

Przesyłanie faktury z użyciem konkretnego profilu:
```bash
$ ksefcli PrzeslijFaktury -c ksefcli.yaml -f d03900-001.xml  -a firma2
```

Wyszukiwanie faktur wystawionych w ostatnim tygodniu i zapisanie wyników do pliku:
```bash
$ ksefcli SzukajFaktur -c ksefcli.yaml --from "-1week" --to "now" > /tmp/1.json
```

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
      private_key: <zawartosc_klucza_prywatnego>
      private_key_file: <sciezka_do_klucza_prywatnego>
      certificate: <zawartosc_certyfikatu_publicznego>
      certificate_file: <sciezka_do_certyfikatu_publicznego>
      password: <haslo_do_klucza_prywatnego>
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
                *   `private_key`: Zawartość klucza prywatnego.
                *   `private_key_file`: Ścieżka do klucza prywatnego (plik `.pem` lub `.pfx`). Można użyć `~` jako skrótu do katalogu domowego.
                *   `certificate`: Zawartość certyfikatu publicznego.
                *   `certificate_file`: Ścieżka do certyfikatu publicznego. Można użyć `~` jako skrótu do katalogu domowego.
                *   `password`: Hasło do klucza prywatnego.
                *   `password_env`: Nazwa zmiennej środowiskowej, która przechowuje hasło do klucza prywatnego.
                *   `password_file`: Ścieżka do pliku z hasłem do klucza prywatnego.

### Przykład Konfiguracji

Poniższy przykład demonstruje konfigurację z wieloma profilami dla różnych podmiotów i środowisk.

```yaml
---
active_profile: firma1
profiles:
  firma1:
    environment: test
    nip: '12312312312'
    token: fdsafa
  firma2:
    environment: demo
    nip: '12312312312'
    token: fdsfa
  firma3:
    environment: prod
    nip: '23434545676'
    token: fdasfa
  cert_auth_example:
    environment: prod
    nip: '1234567890'
    certificate:
      private_key_file: '~/certs/my_private_key.pem'
      certificate_file: '~/certs/my_certificate.pem'
      password_env: 'KSEF_CERT_PASSWORD'

```

W tym przykładzie:
- Domyślnym profilem jest `firma1`.
- Zdefiniowano trzy profile (`firma1`, `firma2`, `firma3`) używające uwierzytelniania tokenem na środowisku testowym dla dwóch różnych NIP-ów.
- Profil `cert_auth_example` używa uwierzytelniania certyfikatem na środowisku produkcyjnym. Hasło do certyfikatu zostanie odczytane ze zmiennej środowiskowej `KSEF_CERT_PASSWORD`.

## Użycie

Ogólna składnia poleceń `ksefcli` jest następująca:

```bash
ksefcli <polecenie> [opcje]
```

### Opcje Globalne

### Dostępne Polecenia

*   `Auth`: Uwierzytelnia przy użyciu skonfigurowanej metody.
*   `TokenAuth`: Uwierzytelnia przy użyciu tokena sesji KSeF.
*   `CertAuth`: Uwierzytelnia przy użyciu certyfikatu kwalifikowanego.
*   `TokenRefresh`: Odświeża istniejący token sesji.
*   `SzukajFaktur`: Wyszukuje faktury na podstawie określonych kryteriów.
*   `PobierzFaktury`: Pobiera faktury na podstawie kryteriów wyszukiwania.
*   `GetFaktura`: Pobiera pojedynczą fakturę po jej numerze KSeF.
*   `PrzeslijFaktury`: Wysyła faktury do KSeF.
*   `LinkDoFaktury`: Generuje link weryfikacyjny dla faktury.
*   `QRDoFaktury`: Generuje kod QR dla linku weryfikacyjnego faktury.
*   `PrintConfig`: Prints the active configuration in YAML or JSON format.
*   `SelfUpdate`: Aktualizuje narzędzie ksefcli do najnowszej wersji.
*   `XML2PDF`: Konwertuje fakturę KSeF w formacie XML na format PDF.

## Polecenia

---

### `Auth`

Uwierzytelnia użytkownika na podstawie metody zdefiniowanej w aktywnym profilu (token lub certyfikat) i zwraca token dostępowy.

**Użycie:**
```bash
ksefcli -a moj_profil Auth
```

---

### `TokenAuth`

Wymusza uwierzytelnienie za pomocą tokena sesyjnego z aktywnego profilu. Profil musi zawierać klucz `token`.

**Użycie:**
```bash
ksefcli -a profil_z_tokenem TokenAuth
```

---

### `CertAuth`

Wymusza uwierzytelnienie za pomocą certyfikatu kwalifikowanego z aktywnego profilu. Profil musi zawierać sekcję `certificate`.

**Użycie:**
```bash
ksefcli -a profil_z_certyfikatem CertAuth
```

---

### `TokenRefresh`

Odświeża istniejący token sesji.

**Użycie:**
```bash
ksefcli -a moj_profil TokenRefresh
```

---

### `GetFaktura`

Pobiera pojedynczą fakturę w formacie XML.

**Użycie:**
```bash
ksefcli GetFaktura <ksef-numer>
```

**Argumenty:**

| Argument      | Opis                  | Wymagane |
|---------------|-----------------------|----------|
| `ksef-numer`  | Numer KSeF faktury.   | Tak      |

---

### `SzukajFaktur`

Wyszukuje faktury na podstawie podanych kryteriów. Odpowiada endpointowi `GET /online/Query/Invoice/Sync`.

**Użycie:**
```bash
ksefcli SzukajFaktur --from "-7days" --subjectType Subject2
```

**Opcje:**

| Opcja                                   | Opis                                                                                                                                     | Domyślnie    | Wymagane |
|-----------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------|--------------|----------|
| `-s`, `--subjectType`                   | Typ podmiotu dla kryteriów filtrowania. Możliwe wartości: `Subject1` (sprzedawca), `Subject2` (nabywca), `Subject3`, `SubjectAuthorized`. | `Subject1`   | Tak      |
| `--from`                                | Data początkowa. Może być datą (np. `2023-01-01`) lub datą względną (np. `-2days`, `'last monday'`).                                       |              | Tak      |
| `--to`                                  | Data końcowa. Może być datą (np. `2023-01-31`) lub datą względną (np. `today`, `-1day`).                                                   |              | Nie      |
| `--dateType`                            | Typ daty używany w zakresie dat. Możliwe wartości: `Issue`, `Invoicing`, `PermanentStorage`.                                               | `Issue`      | Tak      |
| `--pageOffset`                          | Przesunięcie strony dla paginacji.                                                                                                       | `0`          | Nie      |
| `--pageSize`                            | Rozmiar strony dla paginacji.                                                                                                            | `10`         | Nie      |
| `--restrictToPermanentStorageHwmDate`   | Ogranicza filtrowanie do `PermanentStorageHwmDate`. Dotyczy tylko `dateType` = `PermanentStorage`.                                     |              | Nie      |
| `--ksefNumber`                          | Numer KSeF faktury (dokładne dopasowanie).                                                                                               |              | Nie      |
| `--invoiceNumber`                       | Numer faktury nadany przez wystawcę (dokładne dopasowanie).                                                                              |              | Nie      |
| `--amountType`                          | Typ filtru kwotowego. Możliwe wartości: `Brutto`, `Netto`, `Vat`.                                                                          |              | Nie      |
| `--amountFrom`                          | Minimalna wartość kwoty.                                                                                                                 |              | Nie      |
| `--amountTo`                            | Maksymalna wartość kwoty.                                                                                                                |              | Nie      |
| `--sellerNip`                           | NIP sprzedawcy (dokładne dopasowanie).                                                                                                   |              | Nie      |
| `--buyerIdentifierType`                 | Typ identyfikatora nabywcy. Możliwe wartości: `Nip`, `VatUe`, `Other`, `None`.                                                            |              | Nie      |
| `--buyerIdValue`                        | Wartość identyfikatora nabywcy (dokładne dopasowanie).                                                                                   |              | Nie      |
| `--currencyCodes`                       | Kody walut, oddzielone przecinkami (np. `PLN,EUR`).                                                                                       |              | Nie      |
| `--invoicingMode`                       | Tryb fakturowania: `Online` lub `Offline`.                                                                                               |              | Nie      |
| `--isSelfInvoicing`                     | Czy faktura jest samofakturowaniem.                                                                                                      |              | Nie      |
| `--formType`                            | Typ dokumentu. Możliwe wartości: `FA`, `PEF`, `RR`.                                                                                      |              | Nie      |
| `--invoiceTypes`                        | Typy faktur, oddzielone przecinkami (np. `Vat`, `Zal`, `Kor`).                                                                             |              | Nie      |
| `--hasAttachment`                       | Czy faktura posiada załącznik.                                                                                                           |              | Nie      |

---

### `PobierzFaktury`

Pobiera wiele faktur na podstawie kryteriów wyszukiwania. Rozszerza polecenie `SzukajFaktur` o opcje zapisywania plików.

**Użycie:**
```bash
ksefcli PobierzFaktury --from "-7days" --subjectType Subject2 -o /tmp/faktury --pdf
```

**Opcje:**
To polecenie akceptuje wszystkie opcje z `SzukajFaktur` oraz dodatkowo:

| Opcja                | Opis                                                            | Wymagane |
|----------------------|-----------------------------------------------------------------|----------|
| `-o`, `--outputdir`  | Katalog wyjściowy do zapisania faktur.                          | Tak      |
| `-p`, `--pdf`        | Zapisz również wersję PDF faktury.                              | Nie      |
| `--useInvoiceNumber` | Użyj `InvoiceNumber` zamiast `KsefNumber` jako nazwy pliku.     | Nie      |

---

### `PrzeslijFaktury`

Wysyła faktury w formacie XML do KSeF.

**Użycie:**
```bash
ksefcli PrzeslijFaktury -f faktura1.xml faktura2.xml
```

**Opcje:**

| Opcja           | Opis                                | Wymagane |
|-----------------|-------------------------------------|----------|
| `-f`, `--files` | Ścieżki do plików XML z fakturami.  | Tak      |

---

### `LinkDoFaktury`

Generuje link weryfikacyjny dla pojedynczej faktury.

**Użycie:**
```bash
ksefcli LinkDoFaktury <ksef-numer>
```

**Argumenty:**

| Argument      | Opis                  | Wymagane |
|---------------|-----------------------|----------|
| `ksef-numer`  | Numer KSeF faktury.   | Tak      |

---

### `QRDoFaktury`

Generuje kod QR dla linku weryfikacyjnego faktury i zapisuje go do pliku.

**Użycie:**
```bash
ksefcli QRDoFaktury <ksef-numer> faktura-qr.png
```

**Argumenty:**

| Argument        | Opis                                      | Wymagane |
|-----------------|-------------------------------------------|----------|
| `ksef-numer`    | Numer KSeF faktury.                       | Tak      |
| `output-path`   | Ścieżka pliku wyjściowego dla kodu QR.    | Tak      |

**Opcje:**

| Opcja            | Opis                                 | Domyślnie |
|------------------|--------------------------------------|-----------|
| `-p`, `--pixels` | Piksele na moduł dla kodu QR.        | `5`       |

---

### `PrintConfig`

Wypisuje aktywną konfigurację w formacie YAML (domyślnie) lub JSON (z opcją `--json`).

**Użycie:**
```bash
ksefcli PrintConfig [--json]
```

**Opcje:**

| Opcja       | Opis                                | Domyślnie |
|-------------|-------------------------------------|-----------|
| `--json`    | Wypisuje konfigurację w formacie JSON. | `false`   |

---

### `SelfUpdate`

Aktualizuje narzędzie `ksefcli` do najnowszej stabilnej wersji, pobierając binarkę z repozytorium GitLab CI/CD.

**Użycie:**
```bash
ksefcli SelfUpdate [--url <adres-url-binarki>]
```

**Opcje:**

| Opcja            | Opis                                                                                   | Domyślnie |
|------------------|----------------------------------------------------------------------------------------|-----------|
| `-d`, `--destination` | Zapisuje nową wersję do określonej ścieżki zamiast zastępować bieżący plik wykonywalny. | Bieżący plik wykonywalny |
| `--url`          | Określa niestandardowy adres URL do pobrania binarnego pliku aktualizacji.              | Automatycznie wykrywany na podstawie platformy |

---

### `XML2PDF`

Konwertuje fakturę KSeF w formacie XML na plik PDF.

**Użycie:**
```bash
ksefcli XML2PDF faktura.xml faktura.pdf
```

**Argumenty:**

| Argument      | Opis                        | Wymagane |
|---------------|-----------------------------|----------|
| `input-file`  | Wejściowy plik XML.         | Tak      |
| `output-file` | Wyjściowy plik PDF.         | Nie      |

## Rozwój

Rozwój odbywa się na GitLabie.

Aby skonfigurować środowisko deweloperskie, wykonaj następujące kroki:

1.  Sklonuj repozytorium:
    ```bash
    git clone https://gitlab.com/kamcuk/ksefcli.git
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
Dokumentacja KSeF API: [https://api-test.ksef.mf.gov.pl/docs/v2/index.html](https://api-test.ksef.mf.gov.pl/docs/v2/index.html).

## Autor i Licencja

Program napisany przez Kamila Cukrowskiego.
Licencja: [GPLv3](LICENSE.md).