# Kontrola integralności ksiąg i wybranych danych (wersja 1.0.0)

## Stan rzeczywiście wdrożony

- **Główna baza: zewnętrzny PostgreSQL, obowiązkowy.** Program wymaga połączenia z nim, aby wykonywać normalne czynności księgowe. Każda firma używa własnej bazy. **MySQL nie jest jeszcze obsługiwany**. Nie należy podawać tej opcji użytkownikom jako działającej.
- **Lokalny SQLite jest wyłącznie pomocniczym rejestrem kontrolnym**, a nie alternatywną bazą księgową ani źródłem danych do synchronizacji zwrotnej. Plik w lokalnym katalogu danych zawiera identyfikatory zaksięgowanych zapisów i ich odciski SHA-256; nie zawiera pełnych dekretów.
- Przy starcie programu oraz co pięć minut program pobiera spójny odczyt zaksięgowanych zapisów PostgreSQL (transakcja REPEATABLE READ), weryfikuje łańcuch i porównuje zapamiętane odciski. Dopiero po pozytywnej weryfikacji dopisuje nowe. Zmiana lub usunięcie już znanego zapisu wywołuje ostrzeżenie. Program nie przepisuje historycznych odcisków ani nie dokonuje korekt samodzielnie.
- **Wszelkie ostrzeżenia są nieblokujące.** W razie alarmu należy zachować stan baz, zabezpieczyć logi i kopie oraz skontaktować się z administratorem. Zaksięgowany zapis poprawia się przez dopuszczalny prawnie zapis korygujący/storno, a nie przez bezpośrednią zmianę treści.
- Zaszyfrowanie wybranych pól tekstowych (PESEL, wybrany IBAN i dane kadrowe) w PostgreSQL ma utrudniać przypadkowy odczyt/modyfikację bezpośrednio w SQL. W kodzie znajduje się deterministyczny, odtwarzalny algorytm CompanyFieldCodec i warstwa EF. Obecny wariant **nie zapewnia poufności wobec administratora mającego kod i identyfikator firmy**: klucz nie jest tajny, gdyż pochodzi wyłącznie z danych firmy. Nie można traktować go jako pełnej ochrony kryptograficznej RODO. Wymagane są dodatkowo uprawnienia bazy, TLS, kopie zapasowe i kontrola dostępu.
- Starsze jawne dane wymagają kontrolowanej migracji z kopią bezpieczeństwa. Kod CompanyFieldMaintenance udostępnia procedurę, ale nie powinno się uznawać istniejących baz za w pełni zaszyfrowane, dopóki administracyjna migracja nie zostanie przeprowadzona i zweryfikowana.

## Istotne ograniczenia

1. Nowe stanowisko nie ma jeszcze lokalnego punktu odniesienia. Pierwsze uruchomienie przyjmuje stan serwera po kontroli wewnętrznej, **nie dowodzi to braku wcześniejszej manipulacji**. Weryfikacja z niezależnym archiwum/administratorem jest konieczna.
2. Osoba mająca pełną kontrolę zarówno nad serwerem, jak i lokalnym plikiem SQLite może zmodyfikować oba. Mechanizm nie jest niezależnym, odpornym na manipulację dziennikiem zdarzeń (WORM).
3. Aktualny lokalny rejestr porównuje **zapisane dekrety księgowe i ich pozycje**; nie obejmuje jeszcze wszystkich pozostałych encji (np. dokumentów źródłowych, rejestrów VAT, załączników, danych kadrowych, ustawień). Odrębne mechanizmy integralności i kopii mogą chronić inne moduły, ale nie wolno przypisywać ich temu rejestrowi.
4. Brak alarmu nie dowodzi prawidłowości prawa podatkowego, list płac, kont, VAT/JPK czy treści księgowania. Wymagane są walidacje merytoryczne, testy i audyty.
5. Lokalny SQLite zawiera jedynie odciski; nadal wymaga kontroli dostępu do komputera i właściwych procedur retencji. Sam w sobie nie stanowi pełnego archiwum księgowego.

## Regulacje

Ważne są wymagania ustawy o rachunkowości dotyczące poprawności, sprawdzalności, trwałości zapisów, korekt, ochrony danych i archiwizacji (m.in. art. 10, 23–25 i 71–74) oraz RODO, w tym art. 5, 25 i 32. Zastosowanie technologii jest co do zasady dopuszczalne, ale zgodności konkretnego wdrożenia nie można potwierdzić samym istnieniem funkcji.
