# Bezpieczeństwo i ograniczenia produkcyjne — Pełna Księgowość 1.0.0

> **Stan: przedprodukcyjny.** Samo przejście CI, posiadanie szyfrowania i triggerów nie dowodzi zgodności z prawem, odporności na włamanie ani możliwości bezpiecznego prowadzenia ksiąg bez dalszych kontroli. Zgłaszanie podatności: [../SECURITY.md](../SECURITY.md). Rejestr ustaleń: [AUDYT_MAIN_2026-10-31_V1.0.0.md](AUDYT_MAIN_2026-10-31_V1.0.0.md).

## 1. Model tożsamości i uprawnień

- Aktywna aplikacja wymaga PostgreSQL. Tożsamością operatora jest osobny, indywidualny login serwera (identyfikator `db:login`). Hasło PostgreSQL znajduje się w lokalnym magazynie sekretów. Blokada sesyjna nie pozwala na jednoczesną pracę dwóch instancji programu na tym samym loginie.
- Aplikacja wymaga braku przywilejów superuser, CREATEROLE, CREATEDB, REPLICATION, BYPASSRLS, własności bazy/tabel oraz CREATE na schemacie public / w bazie. **Nie wolno używać właściciela bazy jako zwykłego operatora.** Administracja i migracje korzystają z oddzielnej roli.
- Jedna baza = jedna jednostka księgowa. Obecne role aplikacyjne i filtry EF nie zapewniają bezpiecznej obsługi wielu jednostek w jednej bazie; aplikacja blokuje taki scenariusz.
- **Otwarte ryzyko P0:** operator, któremu przyznano bezpośrednie `INSERT/UPDATE/DELETE` do tabel biznesowych, może użyć zewnętrznego klienta SQL i ominąć uprawnienia aplikacji. Nie można usunąć tej luki samymi filtrami EF, TLS, triggerami ani hasłami użytkowników. Do pełnej izolacji operacji potrzebny jest zaufany backend/API wykonujący operacje na serwerze, a dla stacji roboczych tylko uprawnienia klienta API. RLS opierające się na dowolnej wartości ustawianej przez klienta nie jest wystarczające.
- Użytkownicy z dostępem do produkcyjnej bazy muszą mieć uprawnienia przyznawane i odbierane imiennie, a operacje administracyjne powinny być rejestrowane niezależnie od klienta desktopowego.

### Uprawnienia dziennika audytowego

Przy **pierwszym** przygotowaniu nowej bazy aplikacja przyznaje operatorowi `SELECT/INSERT` do `public."AuditEvents"`, lecz odbiera `UPDATE/DELETE`. Pozwala to dopisywać ślad audytowy bez bezpośredniego edytowania i usuwania historii. Jest to dodatkowa warstwa ochrony obok triggerów — **nie usuwa ryzyka P0**, ponieważ operator mający bezpośrednie prawo `INSERT` do bazy nadal może omijać logikę aplikacji i fałszować nowe wpisy.

**Istniejące bazy nie są automatycznie modyfikowane.** Administrator musi po wykonaniu kopii i przeglądzie ról odebrać efektywne uprawnienia `UPDATE/DELETE` do `public."AuditEvents"` operatorom i rolom, z których dziedziczą uprawnienia. Kontrolę przeprowadza `scripts/postgres-operator-preflight.sql`, uruchamiany pod kontem operatora. Nie nadawaj operatorowi własności tabel ani uprawnień `TRIGGER`; wyzwalacze nie zastępują kontroli dostępu.

### Minimalna kontrola konfiguracji DBA

Wykonuj poniższe zapytania **pod kontem operatora**, nie administratora:

```sql
SELECT session_user, current_database();
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
FROM pg_roles WHERE rolname = session_user;
SELECT has_database_privilege(current_database(), 'CREATE') AS can_create_in_database;
SELECT has_schema_privilege('public', 'CREATE') AS can_create_in_public_schema;
SELECT n.nspname, c.relname, pg_has_role(session_user::regrole, c.relowner, 'MEMBER') AS owns_table
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind IN ('r','p')
ORDER BY c.relname;
```

Wynik administracyjnego uprawnienia lub własności tabel powinien blokować normalną sesję. Nie kopiuj przykładowego przydziału szerokich uprawnień DML do środowiska produkcyjnego jako „gotowego zabezpieczenia”. Przed produkcją udokumentuj oddzielną, zaufaną ścieżkę autoryzacji zapisów.

## 2. Szyfrowanie i sekrety

- **PostgreSQL:** na zdalnych połączeniach wymagany jest TLS `VerifyFull`, obejmujący certyfikat CA i nazwę hosta. Szyfrowanie dysków, magazynu danych, zrzutów narzędziowych i serwerowych kluczy ustala i kontroluje administrator infrastruktury.
- **SQLite:** SQLCipher szyfruje testowe, migracyjne i odtwarzane pliki. Migracja jawnej bazy weryfikuje kopię przed zamianą pliku; sama zamiana nie gwarantuje fizycznego usunięcia historycznych bloków SSD. Stosuj BitLocker lub LUKS.
- **Załączniki:** AES-256-GCM i sumy SHA-256; lokalne pliki nie stają się automatycznie dostępne na innych stanowiskach. Klucze muszą być odtwarzalne i przenoszone bezpiecznym kanałem.
- **Kopie:** AES-256-GCM (uwierzytelniane fragmenty), hasło przetwarzane PBKDF2 albo losowy klucz odzyskiwania. Archiwum wymaga osobnego zabezpieczenia przed skasowaniem i przed ransomware.
- **Windows:** Credential Manager. **Linux:** Secret Service lub awaryjny lokalny magazyn z osobnym plikiem klucza i szyfrogramem pod tym samym kontem. Kompromitacja tego konta może ujawnić oba pliki; preferuj systemowy magazyn kluczy.

## 3. Integralność i księgowość

- Zaksięgowane zapisy mają ograniczenia zmian, storno, unikatową numerację oraz łańcuchy SHA-256. SHA-256 nie jest podpisem cyfrowym ani dowodem odporności na administratora bazy, który może przeobliczyć historię.
- Kontrole rachunkowe muszą obejmować ślad dokument–zapis–korekta, otwieranie i zamykanie okresów, politykę rachunkowości jednostki, ewidencję dowodów i niezależną weryfikację wyliczeń podatkowych.
- W module P.S.A. dokumentacja dopuszczalności wypłaty i prognozy sześciomiesięcznej jest wymagana, ale program **nie przeprowadza automatycznie pełnego testu prawnego limitów wypłaty**. Konieczna jest niezależna ocena art. 300¹⁵ KSH na podstawie sprawozdania, uchwał, kapitału i prognozy płynności.

## 4. Backup i odtwarzanie

- PostgreSQL: tabele, sumy i metadane załączników są eksportowane w jednym snapshotcie `REPEATABLE READ`; załączniki kontrolowane są sumą treści. Odrzucany jest niekompletny zestaw dokumentów.
- Przywracanie bazy serwerowej wymaga pustej bazy oraz pustego magazynu załączników. Załączniki są kopiowane przed importem bazy, a liczności tabel i sumy księgowe sprawdzane są **w transakcji przed commit**. Awaria po rozpoczęciu przywracania może zostawić pliki w tymczasowym katalogu docelowym, które wymagają usunięcia przed kolejną próbą; nie udostępniaj celu do pracy, dopóki nie potwierdzisz pełnej poprawności.
- Przywracanie SQLite przygotowuje zaszyfrowany wynik i załączniki w katalogu przejściowym na docelowym woluminie. Dopiero po weryfikacji przenosi katalog do wskazanego miejsca; nie nadpisuje istniejących plików.
- Szyfrowane archiwa wymagają uwierzytelnienia, sprawdzenia struktury, manifestu, kompletności tabel, rozmiarów i sum; duplikaty ścieżek ZIP są odrzucane.
- **P1 otwarte:** nie ma udokumentowanego testu awaryjnego odtworzenia produkcyjnego PostgreSQL i wszystkich załączników na niezależnym serwerze. Należy zdefiniować RPO/RTO, kopiowanie 3-2-1, odseparowaną kopię/retencję WORM oraz regularne próby odtworzenia.

## 5. KSeF, RODO i aktualizacje

- Operacja wysyłki faktury do KSeF nie jest automatycznie ponawiana po niejednoznacznej odpowiedzi. Stan rozpoczętej próby zapisywany jest przed POST. Konieczne jest sprawdzenie wyniku w KSeF i odtworzenie statusu przed kolejną próbą.
- Okresy retencji, umowy powierzenia, upoważnienia i procedura reagowania na incydenty są wymaganiami organizacyjnymi. Art. 32 RODO wymaga również okresowych prób przywrócenia dostępu i testów zabezpieczeń.
- Import XSD ogranicza HTTPS, wielkość archiwum i plików oraz zabrania DTD; zmiany pobranych schematów powinny być ręcznie porównane z aktualnymi wymaganiami MF.
- GitHub CI wykonuje kompilację i testy, ale **ochrona gałęzi main musi zostać skonfigurowana w ustawieniach repozytorium** (wymagane testy, przegląd zmian, ochrona przed bezpośrednimi pushami). Ta konfiguracja nie jest zastępowana kodem workflow.

## 6. Warunek dopuszczenia produkcyjnego

Brak otwartych blokad P0, pełna izolacja SQL przez zaufaną warstwę autoryzacji, zielone testy dokładnego commita na obu systemach, test awaryjnego odzyskania ksiąg i dokumentów, podpisana dokumentacja rachunkowa jednostki, potwierdzenie wyników księgowych i podatkowych z niezależnymi przykładami oraz zakończony przegląd RODO. Dopóki nie ma takich dowodów, projekt pozostaje przedprodukcyjny.
