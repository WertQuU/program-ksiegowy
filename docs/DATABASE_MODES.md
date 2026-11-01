# Wybór bazy i import SQLite

Wersja aplikacji pozostaje 1.0.0. Kreator w kroku 2 udostępnia:

| Silnik | A — nowa baza | B — istniejąca baza |
|---|---|---|
| SQLite | Nowy, szyfrowany plik i aktualny schemat; odmowa nadpisania istniejącego pliku | Wymagany istniejący plik; import klucza na innym komputerze |
| PostgreSQL | Osobne konto administratora tworzy bazę, schemat i ograniczone konto operatora; brak zapisu hasła administratora | Ograniczone konto operatora, aktualny schemat; brak automatycznego DDL |

Migracja SQLite jest opcjonalna w obu trybach. Cel musi być pusty. Źródło musi być zamknięte (bez WAL/SHM/journal) i zawierać jedną jednostkę. Migracja wymaga uprawnienia zarządzania użytkownikami w źródle. Podaj klucz źródłowej bazy, katalog załączników i, jeśli potrzebny, klucz załączników. Program pracuje na prywatnej kopii źródła, wykonuje szyfrowany backup oraz porównuje liczby rekordów, sumy kontrolne i łańcuch księgowań przed zatwierdzeniem transakcji importu. Dla nowego loginu PostgreSQL przenosi role uprawnionego operatora źródłowego.

W razie błędu nie zatwierdza danych ksiąg; pliki załączników skopiowane przed błędem mogą pozostać w celu. Ponowienie dopuszcza wyłącznie pliki o zgodnych sumach. Nie usuwa ani nie nadpisuje źródłowej bazy. Szyfrowana kopia źródła pozostaje w wybranym katalogu kopii. Nowy klucz odzyskiwania trzeba zapisać poza komputerem.

Załączniki PostgreSQL nie są automatycznie współdzielone między komputerami. Wybierz uzgodniony magazyn i klucze. Opcja SQLite oznacza samodzielną pracę lokalną; nie udostępniaj aktywnego pliku przez katalog synchronizowany.
