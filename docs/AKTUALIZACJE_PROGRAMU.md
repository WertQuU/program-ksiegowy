# Aktualizacje programu z GitHub Releases

Stan kodu: wersja **1.0.0**. Aktualizator znajduje się w aplikacji: **Ustawienia → Aktualizacje programu**.

## Dla użytkownika

1. Wybierz **Sprawdź aktualizacje na GitHub**. Program pobiera metadane najnowszego **opublikowanego, stabilnego** wydania `WertQuU/program-ksiegowy`. Nie aktualizuje się z commitów `main`, wersji roboczych ani prerelease.
2. Jeżeli numer wersji jest większy od zainstalowanego, program wskazuje dostępny pakiet odpowiedni dla architektury **x64**. Na Windows wybiera instalator **MSI**, a na Debianie/Ubuntu pakiet **DEB**.
3. Po świadomym zatwierdzeniu pobiera plik do odrębnego katalogu tymczasowego, porównuje rozmiar, sumę **SHA-256** z `SHA256SUMS.txt` oraz, jeśli GitHub ją udostępnia, sumę z metadanych API. Niedopasowanie oznacza odmowę uruchomienia instalatora.
4. Na Windows uruchamia systemowy **Windows Installer** (`msiexec.exe`, z kontrolą UAC); na Linuksie próbuje otworzyć pobrany DEB w skojarzonym narzędziu systemowym (`xdg-open`). Menedżer pakietów może wymagać uprawnień administratora. Brak obsługi `xdg-open` oznacza konieczność ręcznego zainstalowania **już pobranego i zweryfikowanego** pliku.
5. **Zapisz pracę, wykonaj kopię ksiąg, zamknij aplikację przed instalacją i uruchom ją ponownie po jej zakończeniu.** Instalator OS nadpisuje pliki programu, **nie** księgi i załączniki. Program nie wykonuje automatycznej migracji schematu PostgreSQL podczas aktualizacji.

Przed aktualizacją wielu stanowisk odłącz użytkowników, wykonaj kopię bazy i załączników oraz uzgodnij zgodność wersji aplikacji i migracji bazy. Administrator powinien zaktualizować schemat w kontrolowanym oknie serwisowym, a przed wznowieniem pracy zweryfikować integralność i odtwarzalność.

## Dla osoby publikującej wydania

Workflow `.github/workflows/ci.yml` buduje pakiety MSI, ZIP, DEB i TAR.GZ oraz umieszcza plik `SHA256SUMS.txt` przy **świadomym** uruchomieniu `workflow_dispatch` z polem `wydanie`. Opublikowane wydanie stable na GitHub musi mieć tag np. `v1.0.1` (trzy segmenty wersji), pakiet `*.msi` i `pelnaksiegowosc_1.0.1_amd64.deb`, a także odpowiadającą im listę sum `SHA256SUMS.txt`.

Nie publikuj kolejnej wersji, zanim nie są zakończone testy, ocena bezpieczeństwa oraz przegląd zgodności. **Aktualizator nie tworzy wydań i nie aktualizuje wersji w repozytorium; samo dodanie tej funkcji nie uprawnia do wydania 1.0.1.**

Przy aktywnym wydaniu `v1.0.0` i programie w tej samej wersji nie będzie proponowana ponowna instalacja. Jeżeli repozytorium nie ma jeszcze żadnego GitHub Release, aplikacja wyświetli brak nowej wersji.

## Ograniczenia bezpieczeństwa

- Wymagane są HTTPS, oficjalna ścieżka wydania tego repozytorium, zgodne sumy SHA-256 i prawidłowy rozmiar pliku. Nie wyłączaj tych kontroli po błędzie sieciowym.
- Sumy kontrolne i instalator są publikowane **w tym samym repozytorium**. Chronią przed uszkodzeniem, nie wykrywają jednak przejęcia konta wydawcy lub podmiany całego wydania przez uprawnionego atakującego. Docelowo wymagane są podpisy Authenticode dla MSI, podpisany manifest wydań osobnym zaufanym kluczem, weryfikacja po stronie aplikacji oraz polityka ochrony tagów/wydań.
- Linux DEB jest otwierany przez menedżer pakietów, nie jest instalowany z uprawnieniami root przez wewnętrzny proces aplikacji. Dla dystrybucji innych niż Debian/Ubuntu i innych architektur pobieranie aktualizacji w programie jest obecnie niedostępne.
- Nie instaluj aktualizacji w trakcie księgowania, nie przerywaj migracji i nie zakładaj, że nowszy kod oznacza nowszy schemat bazy.
- Release jest publikowany wyłącznie na polecenie opiekuna projektu; nie publikujemy automatycznie zmian wprowadzanych na `main`.

## Testy

`tests/PelnaKsiegowosc.Tests/Integrations/GitHubReleaseUpdaterTests.cs` obejmuje brak opublikowanego wydania, porównywanie wersji, obecność i spójność listy sum oraz wykrywanie uszkodzonego pobrania przy niezmienionym rozmiarze. Wszystkie testy HTTP korzystają z atrap; do faktycznego przetestowania GUI, UAC i menedżera pakietów potrzebne są dodatkowe testy instalacyjne na Windows i Linuksie.
