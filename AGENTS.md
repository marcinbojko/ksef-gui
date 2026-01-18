# Projekt ksefcli

Projekt `ksefcli` to aplikacja CLI na system Linux, napisana w C#, służąca do interakcji z API Krajowego Systemu e-Faktur (KSeF) w Polsce. Wykorzystuje ona bibliotekę kliencką `ksef-client-csharp` do komunikacji z usługami KSeF.

## Zasady pracy agenta

Jako agent odpowiedzialny za rozwój tego projektu, będę przestrzegał następujących zasad:

*   **Nie wykonuję poleceń automatycznie bez planu**: Zawsze przedstawiam plan działania przed rozpoczęciem implementacji.
*   **Generuję listę TODO**: Dla złożonych zadań, najpierw tworzę szczegółową, techniczną listę kroków (`TODO`) przy użyciu narzędzia `write_todos`.
*   **Realizuję punkty TODO jeden po drugim**: Pracuję metodycznie, wykonując zadania z listy `TODO` sekwencyjnie.

## Inicjalizacja repozytorium

Repozytorium zostało zainicjalizowane ze strukturą projektu, `.gitignore` dla C#/.NET/Linux oraz plikiem `AGENTS.md`. Główna gałąź to `main`.

## Analiza zależności ksef-client-csharp

Biblioteka kliencka `ksef-client-csharp` będzie integrowana z projektem `ksefcli` poprzez pakiety NuGet hostowane na GitHub Packages organizacji CIRFMF. Wymaga to konfiguracji NuGet do dostępu do GitHub Packages oraz użycia Personal Access Token (PAT) z uprawnieniami `read:packages`.

Kluczowe komponenty do interakcji z KSeF, dostarczane przez bibliotekę, to:
*   **Autoryzacja i tokeny**: Zarządzanie procesem autoryzacji, generowaniem i odświeżaniem tokenów (prawdopodobnie przez klasy `KSeFClient`, `CertificateFetcherServices`, `CryptographyServices`).
*   **Wysyłanie faktur XML**: Funkcjonalność do przesyłania plików XML faktur do KSeF.
*   **Pobieranie i wyszukiwanie faktur**: Możliwość pobierania faktur oraz ich wyszukiwania z użyciem filtrów.

Szczegóły dotyczące wymaganych konfiguracji (certyfikaty, zmienne środowiskowe) zostaną doprecyzowane na etapie implementacji obsługi konfiguracji (TODO 04).

## Struktura Projektu CLI (ksefcli)

Projekt `ksefcli` będzie miał następującą strukturę katalogów, zgodnie z `prompt2.md`:

```
ksefcli/
├── src/
│   ├── KsefCli/
│   │   ├── Program.cs           # Bootstrap aplikacji
│   │   ├── Cli/                 # Definicje głównych komend i opcji CLI
│   │   │   ├── RootCommand.cs
│   │   │   └── CommonOptions.cs
│   │   ├── Commands/            # Implementacje logiki dla konkretnych komend
│   │   │   ├── Auth/
│   │   │   │   ├── AuthCommand.cs
│   │   │   │   └── TokenRefreshCommand.cs
│   │   │   ├── Faktura/
│   │   │   │   ├── FakturaCommand.cs
│   │   │   │   ├── UploadCommand.cs
│   │   │   │   └── ListCommand.cs
│   │   ├── Services/            # Logika biznesowa i integracja z API KSeF
│   │   │   ├── AuthService.cs
│   │   │   ├── InvoiceService.cs
│   │   │   └── TokenStore.cs
│   │   ├── Config/              # Obsługa konfiguracji aplikacji
│   │   │   ├── AppConfig.cs
│   │   │   └── ConfigLoader.cs
│   │   ├── Output/              # Formatowanie i prezentacja wyników
│   │   │   ├── TablePrinter.cs
│   │   │   └── ErrorPrinter.cs
│   │   └── ExitCodes.cs         # Definicje kodów wyjścia aplikacji
│   └── KsefCli.Tests/           # Projekt z testami jednostkowymi i integracyjnymi
│       ├── Cli/
│       ├── Commands/
│       └── Services/
├── doc/                         # Dodatkowa dokumentacja (np. architektury, auth flow)
│   ├── architecture.md
│   ├── auth.md
│   └── faktura.md
├── AGENTS.md                    # Dokument sterujący projektem
├── GEMINI.md -> AGENTS.md       # Symlink do AGENTS.md
├── Dockerfile                   # Definicja środowiska Docker (build, test, runtime)
├── .editorconfig                # Zasady formatowania kodu
├── Directory.Build.props        # Globalne ustawienia kompilacji i analizy
├── Directory.Build.targets      # Dodatkowe cele kompilacji (np. dla formatowania)
└── ksefcli.sln                  # Plik rozwiązania Visual Studio
```

## Zasady Architektoniczne

*   `Program.cs` będzie zawierał wyłącznie kod startowy (bootstrap) aplikacji, bez logiki biznesowej.
*   Klasy komend CLI będą cienkimi warstwami odpowiedzialnymi jedynie za parsowanie argumentów, walidację i delegowanie pracy do odpowiednich serwisów.
*   Logika biznesowa będzie zaimplementowana w klasach serwisów (`Services/`), które będą integrować się z biblioteką `ksef-client-csharp`.
*   Unikane będą statyczne singletony na rzecz Dependency Injection.
*   Projekt będzie maksymalnie testowalny, z mockowaniem zewnętrznych zależności (np. API KSeF).

## Best Practices C# / .NET

*   Włączone `Nullable Reference Types`.
*   Użycie `async/await` we wszystkich operacjach I/O, bez blokowania `Task.Result` czy `Wait()`.
*   Jawne typy w publicznym API.
*   Domyślny modyfikator dostępu `internal`, `public` tylko, gdy jest to niezbędne.
*   Brak logiki w konstruktorach, aby ułatwić testowanie i zarządzanie cyklem życia obiektów.

## Parser Argumentów CLI: Spectre.Console.Cli

Dla parsowania argumentów CLI wybrano bibliotekę **Spectre.Console.Cli**. Jest to nowoczesna i rozbudowana biblioteka, która oferuje intuicyjny sposób definiowania komend, argumentów i opcji, a także zapewnia eleganckie formatowanie wyjścia w terminalu.

Struktura CLI została zaimplementowana w `Program.cs` przy użyciu `CommandApp` i metody `AddBranch` dla zagnieżdżonych komend `auth` i `faktura`. Konkretne podkomendy (`token refresh`, `faktura wyslij`, `faktura ls`) są implementowane jako klasy dziedziczące z `AsyncCommand<TSettings>`, co zapewnia modularność i łatwość rozbudowy.

## Konfiguracja Aplikacji

Konfiguracja aplikacji `ksefcli` jest ładowana z wielu źródeł, zapewniając elastyczność i możliwość nadpisywania ustawień:
1.  **`appsettings.json`**: Plik JSON znajdujący się w katalogu głównym aplikacji.
2.  **Zmienne środowiskowe**: Zmienne środowiskowe z prefiksem `KSEFCLI_` (np. `KSEFCLI_KsefApi__BaseUrl`).
3.  **Argumenty wiersza poleceń**: Argumenty przekazane do aplikacji CLI.

Konfiguracja jest parsowana i bindowana do obiektu `AppConfig` (zdefiniowanego w `src/KsefCli/Config/AppConfig.cs`). `ConfigLoader.cs` jest odpowiedzialny za proces ładowania i podstawową walidację konfiguracji. Walidacja zapewnia, że krytyczne ustawienia (np. `KsefApi.BaseUrl`) są obecne przed uruchomieniem aplikacji.

Wnioski z analizy `ksef-client-csharp` i jego integracji, wraz z dalszymi decyzjami architektonicznymi, będą rozwijane w kolejnych sekcjach tego dokumentu.
