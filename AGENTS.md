# Projekt ksefcli

Projekt `ksefcli` to aplikacja CLI na system Linux, napisana w C#, służąca do interakcji z API Krajowego Systemu e-Faktur (KSeF) w Polsce. Wykorzystuje ona bibliotekę kliencką `ksef-client-csharp` do komunikacji z usługami KSeF.

## Zasady pracy agenta

Jako agent odpowiedzialny za rozwój tego projektu, będę przestrzegał następujących zasad:

*   **Generuję listę TODO**: Wszystkie zadania od użytkownika, dodaję do TODO przy użyciu narzędzia `write_todos`. Następnie kontynuuję wykonywanie zadań z listy TODO.
*   **Realizuję punkty TODO jeden po jednym**: Pracuję metodycznie, wykonując zadania z listy `TODO` sekwencyjnie.
*   **Format listy TODO**: Lista zadań będzie zwięzła, wykorzystując tylko słowa kluczowe i nie będzie zawierać pełnych zdań. Wszystkie podzadania zostaną spłaszczone i przedstawione jako niezależne zadania.
*   **Minimalizm w komunikacji**: Bądź tak zwięzły, jak to tylko możliwe, i wypisuj minimalną ilość informacji, bez gramatyki.
*   **Bezpośrednie narzędzia**: Wykonuj narzędzia bezpośrednio, bez wyjaśnień.
*   **Commit often**: Commituj zmiany często, po każdej znaczącej zmianie.
*   **Brak pustych linii**: Pisząc kod, nie twórz linii bez zawartości.
*   **Nie łap excpetion**: Nie łap exception, tylko po to żeby print. Daj exception raise.



W thirdparty/ksef-client-csharp jest zależność.


## Best Practices C# / .NET

Nie używam `var`. Preferuję typy.

Używaj przestrzeni nazw zadeklarowanych w pliku (`file-scoped namespaces`) zamiast w blokach.

## Parser Argumentów CLI: CommandLineParser

Struktura CLI została zaimplementowana w `Program.cs`
