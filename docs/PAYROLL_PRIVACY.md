# Ochrona danych kadrowo-płacowych

Wersja 1.0.0, zakres issue #7.

## Minimalizacja

W ustawieniach stawki chorobowego przechowywane są wyłącznie stawka 80/100%, liczba potwierdzonych dni u innych pracodawców, odnośnik do dowodu, data i autor potwierdzenia. Kategoria przyczyny (w tym ciąża) nie jest zapisywana. Formularz pozwala wybrać ogólną stawkę; dokument potwierdzający uprawnienie pozostaje w uprawnionej dokumentacji kadrowej. Nie wpisuj diagnoz ani danych medycznych w odnośnikach i uwagach.

Przy odświeżeniu sesji administratora lub głównego księgowego program usuwa pole Cause ze starszych potwierdzeń bieżącej jednostki, zachowując równoważną stawkę i dowód. Zmiana jest odnotowana w audycie. Odtworzenie starej kopii uruchamia tę samą minimalizację przy otwarciu. Nieprawidłowe historyczne stawki wymagają ponownego potwierdzenia.

## Uprawnienia i odczyty

Dostęp do kadr i płac wymaga ViewPayrollData; zmiany dodatkowo ManagePayrollData. Domyślnie otrzymują je Administrator, Główny księgowy i osobna rola Kadry i płace. Sam Księgowy, Fakturzysta, Audytor lub Zarząd tych uprawnień nie otrzymuje. Administrator może nadać dodatkową rolę kadrową imiennie. Rola Kadry i płace nie daje zarządzania użytkownikami ani pełnymi kopiami.

Otwarcie kartotek, odczyt pozycji listy i danych pracownika, odświeżenie nieobecności, generowanie raportu i eksport są rejestrowane bez kopiowania przyczyny choroby do audytu. Raporty płacowe sprawdzają uprawnienie przed zapytaniem i zapisują zdarzenie odczytu; wymagają zapisania innych bieżących zmian, aby logowanie nie utrwaliło przypadkiem obcej operacji. Odczyty wewnętrzne podczas naliczania należą do audytowanej operacji zmiany/księgowania listy.

Pełny backup, jego odtwarzanie oraz udostępnienie kluczy bazy/załączników wymagają odpowiednich praw administracyjnych i dostępu do danych kadrowych. Kopia lub klucz umożliwia dostęp do całego archiwum, więc samo uprawnienie zwykłego księgowego do backupów nie wystarcza. Kontrole są egzekwowane w aplikacji; nie zastępują praw plików systemu operacyjnego ani zaufanej warstwy serwera wobec bezpośredniego SQL. Zmiana ról staje się widoczna w danej sesji po odświeżeniu danych operatora.

## Retencja i odtworzenie

Obsługa żądania usunięcia danych pracownika nadal wymaga upływu ustawowej retencji oraz braku blokady prawnej. Dla zatrudnienia od 2019 r. domyślnie 10 lat od końca roku ustania zatrudnienia, dla wcześniejszego zatrudnienia 50 lat bez automatycznego skracania OSW/RIA. Wraz z anonimizacją usuwane są ustawienia potwierdzeń chorobowego, przypisane grafiki oraz nieobecności i ich uwagi. Zapisy księgowe pozostają zgodnie z obowiązkami ich przechowywania. Uprawnienia do anonimizacji ma administrator/główny księgowy, nie dowolny operator.

Istniejących szyfrowanych kopii nie przepisujemy wstecz. Zawarte w nich dawne kategorie znikają wraz z kontrolowaną rotacją i ograniczeniami retencji; administrator musi zabezpieczyć archiwa, klucze i zewnętrzną dokumentację. Po odtworzeniu starej kopii należy ponownie zastosować zrealizowane żądania anonimizacji i blokady prawne przed udostępnieniem danych; automatyczna synchronizacja rejestru takich żądań między historycznymi kopiami nie jest obecnie dostępna.

## Podstawa i granice

Dane niezbędne do obowiązków płatnika/pracodawcy: art. 6 ust. 1 lit. c i właściwy wyjątek art. 9 ust. 2 lit. b RODO w związku z konkretnymi przepisami prawa pracy i ubezpieczeń; zasady minimalizacji i ograniczenia przechowywania z art. 5, ochrona domyślna art. 25, zabezpieczenia art. 32. Administrator danych musi potwierdzić podstawę, upoważnienia, okresy przechowywania i zakres dla swojej organizacji. Funkcje programu nie są certyfikacją zgodności z RODO.

Źródło urzędowe: UODO, Ochrona danych osobowych w miejscu pracy — poradnik: https://uodo.gov.pl/pl/file/4933 . Tekst RODO: https://eur-lex.europa.eu/eli/reg/2016/679/oj?locale=pl .
