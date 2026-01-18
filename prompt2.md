Analiza README i rekomendowana struktura projektu C# (ksefcli)

Ten dokument opisuje, jak powinien wyglądać dojrzały projekt C# CLI na Linuxa, inspirowany repozytorium ksef-client-csharp, z zastosowaniem best practices, automatycznego formatowania i statycznej analizy kodu.

1. Charakter projektu

Typ: aplikacja CLI (console application)

Platforma docelowa: Linux x86_64

Język: C# (.NET 8)

Styl: produkcyjny, nie przykładowy

Dystrybucja: self-contained / NativeAOT

Projekt CLI nie jest biblioteką. Biblioteką jest ksef-client-csharp. ksefcli pełni rolę cienkiej warstwy:

parsowanie argumentów

walidacja

mapowanie CLI → API

prezentacja wyników

2. Rekomendowana struktura katalogów
ksefcli/
├── src/
│   ├── KSeFCli/
│   │   ├── Program.cs
│   │   ├── Cli/
│   │   │   ├── RootCommand.cs
│   │   │   └── CommonOptions.cs
│   │   ├── Commands/
│   │   │   ├── Auth/
│   │   │   │   ├── AuthCommand.cs
│   │   │   │   └── TokenRefreshCommand.cs
│   │   │   ├── Faktura/
│   │   │   │   ├── FakturaCommand.cs
│   │   │   │   ├── UploadCommand.cs
│   │   │   │   └── ListCommand.cs
│   │   ├── Services/
│   │   │   ├── AuthService.cs
│   │   │   ├── InvoiceService.cs
│   │   │   └── TokenStore.cs
│   │   ├── Config/
│   │   │   ├── AppConfig.cs
│   │   │   └── ConfigLoader.cs
│   │   ├── Output/
│   │   │   ├── TablePrinter.cs
│   │   │   └── ErrorPrinter.cs
│   │   └── ExitCodes.cs
│   └── KSeFCli.Tests/
│       ├── Cli/
│       ├── Commands/
│       └── Services/
├── doc/
│   ├── architecture.md
│   ├── auth.md
│   └── faktura.md
├── AGENTS.md
├── GEMINI.md -> AGENTS.md
├── Dockerfile
├── .editorconfig
├── Directory.Build.props
├── Directory.Build.targets
└── ksefcli.sln
3. Zasady architektoniczne

Program.cs zawiera tylko bootstrap

Brak logiki biznesowej w klasach CLI

Każda komenda CLI mapuje się na jedną klasę

Komendy są cienkie, serwisy robią pracę

Brak statycznych singletonów

Wszystko możliwe do przetestowania bez API

4. Best practices C# / .NET

Nullable Reference Types: enabled

async/await wszędzie gdzie I/O

Brak Task.Result / Wait()

Jawne typy w API publicznym

internal domyślnie, public tylko gdy trzeba

Brak logiki w konstruktorach

5. Automatyczne formatowanie
.editorconfig (obowiązkowe)

Styl Microsoft

4 spacje

newline na końcu pliku

brak trailing whitespace

uporządkowane usingi

Formatter:

dotnet format

uruchamiany lokalnie i w CI

6. Static analysis / linting
Wbudowane analyzery .NET

Włączone globalnie w Directory.Build.props:

TreatWarningsAsErrors = true

AnalysisLevel = latest

EnableNETAnalyzers = true

Reguły

CAxxxx (Code Analysis)

IDExxxx (Style)

SYSLIBxxxx (API deprecations)

Opcjonalnie

StyleCop.Analyzers

tylko reguły strukturalne

bez dokumentacji XML na siłę

7. Testy

xUnit

FluentAssertions

Moq / NSubstitute

Zasady:

testy CLI nie uruchamiają procesu

parser testowany jako obiekt

API mockowane

testy integracyjne oznaczone kategorią

8. Docker jako źródło prawdy

Dockerfile:

stage: restore

stage: build

stage: test

stage: runtime

Docker służy do:

powtarzalnych buildów

testów

manualnego testowania CLI

9. NativeAOT i Linux

Target: linux-musl-x64

PublishAot = true

Brak reflection-heavy bibliotek

Jawne zależności

10. README – co powinno się w nim znaleźć

README projektu nie opisuje kodu linijka po linijce.

Powinno zawierać:

czym jest ksefcli

jakie problemy rozwiązuje

przykłady użycia CLI

wymagania systemowe

jak zbudować (dotnet / docker)

jak uruchomić testy

gdzie jest dokumentacja (doc/)

Bez lania wody.

11. AGENTS.md jako dokument sterujący

opis projektu

zasady pracy (TODO → wykonanie)

decyzje architektoniczne

kompromisy

ograniczenia (KSeF, AOT, API)

AGENTS.md jest ważniejszy niż README.

Gotowe. Analiza README i kompletna wizja projektu C# została zapisana w canvasie.

To nie jest marketingowy README, tylko techniczny kontrakt:

jasno oddziela CLI od biblioteki KSeF

narzuca strukturę katalogów

definiuje architekturę, formatter, linter, testy i Docker jako źródło prawdy

ustawia AGENTS.md jako dokument sterujący, nie ozdobę

Jeśli coś w tym dokumencie jest sprzeczne z realnymi ograniczeniami ksef-client-csharp albo NativeAOT, to jest błąd w założeniach, nie w opisie. Wtedy trzeba to wskazać i skorygować, nie omijać.

analizuj readme w projekcie ksef-client-csharp

Przeanalizowałem README z repozytorium ksef-client-csharp i dopasowuję go do Twojego projektu CLI. To musi trzeźwo uchwycić scope biblioteki, kto co robi i jak to wykorzystać w praktyce.

1. Czym jest ksef-client-csharp
Repo to referencyjna biblioteka .NET do integracji z API KSeF 2.0, zawierająca:

implementację komunikacji HTTP, modele danych i wyjątki

serwisy biznesowe (auth, tokeny, certyfikaty, QR, sesje)

integrację z DI (Dependency Injection) w .NET

struktura modułów jest podzielona na Core i Client części

testy jednostkowe, funkcjonalne i E2E są załączone
Całość jest w C#/.NET 8/9 i wykorzystuje mechanizmy DI, RestClient i wzorce serwisów.

2. Główne foldery i zawartość

KSeF.Client – implementacja klienta z logiką API

KSeF.Client.Core – modele, interfejsy i wyjątki

KSeF.Client.ClientFactory – fabryki do konfigurowania klienta i serwisów

Tests – różne testy, w tym integracyjne i narzędzia

przykładowa aplikacja KSeF.DemoWebApp (ASP.NET)
To pokazuje, że biblioteka nie zawiera CLI, tylko mechanizmy SDK, które trzeba wykorzystać.

3. Instalacja i użycie biblioteki
Biblioteka jest dostępna jako pakiety NuGet (Core i Client). Wymaga:

skonfigurowanego dostępu do GitHub Packages

PAT (Personal Access Token) do pobrania paczek
Integracja z DI w .NET pokazana jest w przykładach:

builder.Services.AddKSeFClient(...)
builder.Services.AddCryptographyClient(...)


To opisuje jak zarejestrować klienta i servis kryptograficzny w kontenerze.

4. Jak biblioteka realnie działa
Po rejestracji klienta biblioteka obsługuje:

generowanie requestów i mapping modeli API

wywołania endpointów auth, faktur, certyfikatów itd.

serwisy kryptograficzne i podpisy (ECDSA i XAdES)

obsługa tokenów i sesji

uwierzytelnianie i odświeżanie tokenów
To oznacza, że sam CLI powinien opakować te funkcje i zmapować do poleceń użytkownika.

5. Wnioski do Twojego projektu CLI
Na bazie README:

(a) ksef-client-csharp nie ma funkcji CLI — musi je zapewnić ksefcli.

(b) Biblioteka już robi ciężką pracę: komunikację z API, serializacje, sesje, tokeny, wyjątki.

(c) CLI musi:

skonfigurować DI i klienta

załadować certyfikaty i opcje

wystawić wygodne komendy (auth, send, list)

obsłużyć błędy i wypisać user-friendly output

(d) Nie ma magicznych helperów CLI w bibliotece — wszystko trzeba opakować.

(e) Testy ksef-client-csharp są solidne i można z nich czerpać przykłady użycia API w testach CLI.

6. Ograniczenia i ryzyka techniczne

wiele endpointów i modeli → wymaga kompletnej mapy CLI → funkcje „query faktur” muszą obsługiwać parametry API

serwis kryptografii i certyfikaty mogą wymagać własnej konfiguracji w CLI (ścieżki, hasła, fallback)

obsługa sesji interaktywnych vs wsadowych → doprecyzować w CLI zakres
To trzeba spisać w AGENTS.md jako scope projektu i ogranczenia.
