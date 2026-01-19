# Projekt ksefcli

Projekt `ksefcli` to aplikacja CLI na system Linux, napisana w C#, służąca do interakcji z API Krajowego Systemu e-Faktur (KSeF) w Polsce. Wykorzystuje ona bibliotekę kliencką `ksef-client-csharp` do komunikacji z usługami KSeF.

## Zasady pracy agenta

Jako agent odpowiedzialny za rozwój tego projektu, będę przestrzegał następujących zasad:

*   **Nie wykonuję poleceń automatycznie bez planu**: Zawsze przedstawiam plan działania przed rozpoczęciem implementacji.
*   **Generuję listę TODO**: Dla złożonych zadań, najpierw tworzę szczegółową, techniczną listę kroków (`TODO`) przy użyciu narzędzia `write_todos`.
*   **Realizuję punkty TODO jeden po jednym**: Pracuję metodycznie, wykonując zadania z listy `TODO` sekwencyjnie.
*   **Format listy TODO**: Lista zadań będzie zwięzła, wykorzystując tylko słowa kluczowe i nie będzie zawierać pełnych zdań. Wszystkie podzadania zostaną spłaszczone i przedstawione jako niezależne zadania.
*   **Minimalizm w komunikacji**: Bądź tak zwięzły, jak to tylko możliwe, i wypisuj minimalną ilość informacji, bez gramatyki.
*   **Commit often**: Commituj zmiany często, po każdej znaczącej zmianie.

## Inicjalizacja repozytorium

Repozytorium zostało zainicjalizowane ze strukturą projektu, `.gitignore` dla C#/.NET/Linux oraz plikiem `AGENTS.md`. Główna gałąź to `main`.

## Analiza zależności ksef-client-csharp

Biblioteka kliencka `ksef-client-csharp` będzie integrowana z projektem `ksefcli` poprzez pakiety NuGet hostowane na GitHub Packages organizacji CIRFMF. Wymaga to konfiguracji NuGet do dostępu do GitHub Packages oraz użycia Personal Access Token (PAT) z uprawnieniami `read:packages`.

Kluczowe komponenty do interakcji z KSeF, dostarczane przez bibliotekę, to:
*   **Wysyłanie faktur XML**: Funkcjonalność do przesyłania plików XML faktur do KSeF.
*   **Pobieranie i wyszukiwanie faktur**: Możliwość pobierania faktur oraz ich wyszukiwania z użyciem filtrów.

Szczegóły dotyczące wymaganych konfiguracji (certyfikaty, zmienne środowiskowe) zostaną doprecyzowane na etapie implementacji obsługi konfiguracji (TODO 04).




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

Konfiguracja jest parsowana i bindowana do obiektu `AppConfig` (zdefiniowanego w `src/KSeFCli/Config/AppConfig.cs`). `ConfigLoader.cs` jest odpowiedzialny za proces ładowania i podstawową walidację konfiguracji. Walidacja zapewnia, że krytyczne ustawienia (np. `KsefApi.BaseUrl`) są obecne przed uruchomieniem aplikacji.

## Model Tokenu i Mechanizm Przechowywania

Aplikacja `ksefcli` wykorzystuje dedykowany model `Token` (zdefiniowany w `src/KSeFCli/Services/Token.cs`) do reprezentowania tokenów autoryzacyjnych KSeF. Model ten zawiera wartość tokenu, datę wygaśnięcia (`ExpiresAt`) oraz identyfikator sesji KSeF (`SessionId`). Zapewnia również pomocnicze właściwości (`IsExpired`, `IsValid`) do łatwej weryfikacji statusu tokenu.

Mechanizm przechowywania tokenów jest realizowany przez klasę `TokenStore` (zdefiniowaną w `src/KSeFCli/Services/TokenStore.cs`). `TokenStore` jest odpowiedzialny za:
*   **Ładowanie i zapisywanie tokenów**: Tokeny są serializowane do formatu JSON (przy użyciu `System.Text.Json`) i zapisywane do pliku. Ścieżka do pliku konfiguracyjnego jest pobierana z `AppConfig.TokenStore.Path`. Domyślnie jest to `~/.config/ksefcli/tokens.json`.
*   **Obsługa ścieżki użytkownika**: Klasa `TokenStore` automatycznie rozwija `~` do katalogu domowego użytkownika, co pozwala na przechowywanie tokenów w standardowej lokalizacji systemowej (`~/.config/ksefcli/`).
*   **Walidacja wygaśnięcia**: Przy ładowaniu tokenu, `TokenStore` sprawdza, czy token nie wygasł, zwracając `null` dla nieprawidłowych lub wygasłych tokenów.
*   **Usuwanie tokenów**: Zapewnia metodę do usuwania przechowywanego pliku tokenu.

## Autoryzacja i tokeny (Auth Flow)

Proces autoryzacji z KSeF wykorzystuje cyfrowy podpis XML (XAdES) przy użyciu certyfikatu klienckiego. Oto kroki:
1.  **Wczytanie certyfikatu**: Aplikacja ładuje certyfikat kliencki (plik `P12` lub `PFX`) z podanej ścieżki (`AppConfig.KsefApi.CertificatePath`) i hasła.
2.  **Pobranie wyzwania (Challenge)**: Aplikacja wysyła żądanie do API KSeF, aby uzyskać unikalne wyzwanie uwierzytelniające (`AuthenticationChallengeResponse`).
3.  **Budowa żądania tokena**: Tworzone jest żądanie tokena (`AuthenticationTokenRequest`) zawierające wyzwanie, typ kontekstu (np. NIP) i typ identyfikatora podmiotu (np. z certyfikatu).
4.  **Serializacja i podpisanie XML**: Żądanie tokena jest serializowane do postaci XML, a następnie podpisywane cyfrowo przy użyciu wczytanego certyfikatu klienckiego i `SignatureService`.
5.  **Wysłanie podpisanego żądania**: Podpisany XML jest przesyłany do API KSeF (`SubmitXadesAuthRequestAsync`), co zwraca `SignatureResponse` zawierające token sesji i numer referencyjny.
6.  **Przechowywanie tokena**: Otrzymany token sesji (`AuthenticationToken.Token`), datę wygaśnięcia (`ValidUntil` z `TokenInfo`) i numer referencyjny operacji (`ReferenceNumber`) są zapisywane lokalnie za pomocą `TokenStore`.

Proces odświeżania tokena działa podobnie:
1.  **Weryfikacja istniejącego tokena**: Aplikacja sprawdza, czy istnieje ważny, nieprzeterminowany token. Jeśli nie, próbuje wygenerować nowy.
2.  **Odświeżenie**: Jeśli token istnieje, ale jest przeterminowany, aplikacja wysyła żądanie odświeżenia tokena (`RefreshAccessTokenAsync`) do API KSeF.
3.  **Przechowywanie nowego tokena**: Nowy token dostępowy (`AccessToken.Token`), jego datę wygaśnięcia (`AccessToken.ValidUntil`) i numer referencyjny operacji są zapisywane lokalnie.

## Dokumentacja

*   **Klonowanie dokumentacji**: Repozytorium `ksef-docs` zostało sklonowane do katalogu `ksef-docs/` w celu zapewnienia dostępu do dodatkowej dokumentacji.

## Zależności zewnętrzne (thirdparty)

*   **`ksef-client-csharp`**: Oficjalny klient KSeF w C#, dodany jako submoduł Git do `thirdparty/ksef-client-csharp/`.
*   **`ksef-docs`**: Oficjalna dokumentacja KSeF, dodana jako submoduł Git do `thirdparty/ksef-docs/`.

Wnioski z analizy `ksef-client-csharp` i jego integracji, wraz z dalszymi decyzjami architektonicznymi, będą rozwijane w kolejnych sekcjach tego dokumentu.
