# Macierz zgodności z przepisami

Statusy: **Z** — zaimplementowane i objęte testami automatycznymi; **Z/W** — zaimplementowane, wymaga weryfikacji
merytorycznej (księgowej, podatkowej lub prawnej) albo testu na rzeczywistych danych/systemie; **Cz** — częściowo;
**N** — nieobsługiwane. Program nie deklaruje zgodności z przepisami — macierz wskazuje, czego dotyczy
implementacja i co wymaga sprawdzenia przez uprawnioną osobę. Brzmienie przepisów należy zweryfikować na dzień użycia.

| Przepis | Wymaganie (skrót) | Realizacja w programie | Status |
|---|---|---|---|
| UoR art. 4 ust. 5 | odpowiedzialność kierownika jednostki za wykonywanie obowiązków w zakresie rachunkowości | wskazanie kierownika w kreatorze; zatwierdzanie polityki | Z |
| UoR art. 9 ust. 1 | księgi w języku polskim i w PLN | waluta ksiąg PLN, kursy walut na dokumentach | Z |
| UoR art. 10 | polityka rachunkowości, plan kont, opis systemu | szablon, blokada księgowania bez zatwierdzenia, raport „Opis systemu informatycznego” | Z/W |
| UoR art. 12 | otwarcie i zamknięcie ksiąg | rok obrotowy, zamknięcie roku, BO | Z |
| UoR art. 13 | dziennik, KG, księgi pomocnicze, ZOiS, wykaz składników | odpowiednie moduły i raporty | Z |
| UoR art. 14 ust. 2–3 | ciągła numeracja zapisów, dzienniki częściowe | numeracja w roku, dzienniki częściowe, ZOiS dzienników | Z |
| UoR art. 18 | zestawienie obrotów i sald na koniec okresu | raport ZOiS, kontrola przy zamknięciu | Z |
| UoR art. 20–22 | dowody księgowe i ich kontrola | dokumenty z zatwierdzeniem i dekretacją | Z/W |
| UoR art. 23 | elementy zapisu, identyfikacja operatora | pola zapisu, operator = konto systemowe | Z |
| UoR art. 24 ust. 4–5 | ochrona ksiąg komputerowych, sprawdzalność | blokada zmian, łańcuch skrótów, dziennik zdarzeń, kontrola integralności | Z/W |
| UoR art. 25 | korekty zapisów | storno czerwone/czarne wg polityki, zapis korygujący | Z |
| UoR art. 26–27 | inwentaryzacja | zestawienie składników i metod, potwierdzenia sald, protokół kasy, arkusz spisu z natury, różnice inwentaryzacyjne w magazynie | Z/W |
| UoR art. 34 ust. 4 | wycena rozchodu zapasów | magazyn: cena przeciętna ważona, PZ/WZ/RW/PW, automatyczne księgowanie rozchodu | Z/W |
| UoR art. 28–35 | wycena aktywów i pasywów | ŚT (amortyzacja), waluty, rozrachunki, odpisy aktualizujące należności (art. 35b — wiekowanie wg polityki, rozwiązanie, spisanie), rezerwy (art. 35d — urlopowa z płac, emerytalna, na koszty, inne), podatek odroczony (art. 37 — środki trwałe, odpisy, rezerwy, straty) | Z/W |
| UoR art. 32 | amortyzacja bilansowa | metody liniowa, degresywna, jednorazowa | Z/W |
| UoR art. 45–46, zał. nr 1 | bilans, RZiS | wariant porównawczy i kalkulacyjny (klucz podziału kosztów rodzajowych), dane porównawcze | Z/W |
| UoR art. 48, 48a, 48b, zał. nr 1 | informacja dodatkowa, zestawienie zmian w kapitale, rachunek przepływów | tabele z ksiąg + opisy użytkownika; RPP metodą pośrednią z kontrolą zmiany środków pieniężnych | Z/W |
| UoR art. 45 ust. 1f i 3, 46 ust. 5, 48a–48b, 50 ust. 3, 52, 64 | sprawozdanie w strukturze logicznej | struktury SFJIN/SFJMA/SFJMI (Z/T) z RZiS wg polityki, zmianami w kapitale, RPP, informacją dodatkową (PDF) i informacją o podatku dochodowym; walidacja schematem; plik „gotowy do podpisu” tylko po kontroli kompletności (rok zamknięty, informacja dodatkowa, brak pól „do uzupełnienia”, konta zmapowane; zestawienie zmian w kapitale i RPP obowiązkowe przy pełnym sprawozdaniu (zał. nr 1) — zależnie od kategorii jednostki, spójnie z generatorem XML; jednostka mała/mikro badana — informacja o uzgodnieniu zakresu z biegłym) — inaczej wersja robocza | Z/W |
| UoR art. 53, 69 | zatwierdzenie i złożenie sprawozdania | projekty uchwał (zatwierdzenie, podział zysku, absolutorium), terminy w kalendarzu | Z/W |
| UoR art. 71–76, art. 10 ust. 1 pkt 4 | ochrona i przechowywanie | szyfrowany plik bazy (SQLCipher AES-256), także bezpośrednio po odtworzeniu z kopii (jawna kopia robocza tylko w prywatnym katalogu tymczasowym), TLS do serwera, szyfrowane kopie tworzone w strumieniu (bez jawnego ZIP), prywatne katalogi robocze, sprzątanie po awarii (także przed aktualizacją schematu), weryfikacja, retencja, okresy przechowywania; role operatorów nadawane przez administratora (rozdział obowiązków), dziennik zdarzeń | Z/W |
| VAT art. 106e | elementy faktury | generator FA(3) w zakresie typowych faktur krajowych i korekt | Z/W |
| VAT art. 86 ust. 10b, 11; art. 106na ust. 3 | termin odliczenia VAT, data otrzymania faktury z KSeF | najwcześniej w okresie otrzymania; miesięcznie — 3 kolejne miesiące, kwartalnie — 2 kolejne kwartały; data otrzymania faktury z KSeF = dzień przydzielenia numeru KSeF w czasie polskim | Z |
| VAT art. 106ga i n. | KSeF | wyłącznie pobieranie faktur zakupu (sprawdzone w praktyce przez użytkownika); faktury sprzedaży wystawiane w KSeF poza programem, w programie wpisywany i walidowany numer KSeF; asystent ostrzega o fakturach dla firm bez numeru KSeF | Z |
| VAT art. 86 | prawo i termin odliczenia | wymagane potwierdzenie zakresu i okresu, kontrola terminów | Z/W |
| VAT art. 109 ust. 3 | ewidencja VAT | rejestry z polami JPK | Z/W |
| Rozporządzenie MF ws. JPK_V7 | struktura JPK_V7M / JPK_V7K | wersje (2) i (3) wg okresu, miesięczny i kwartalny, osoba fizyczna i niefizyczna; walidacja oficjalnymi schematami w testach | Z |
| VAT art. 99 ust. 2, art. 103 ust. 2 | rozliczenie kwartalne | deklaracja za kwartał w ostatnim miesiącu, ewidencja miesięczna | Z/W |
| VAT art. 100 | informacja podsumowująca VAT-UE | zestawienie WDT, WNT, usług (art. 100 ust. 1 pkt 4) wg kontrahentów | Z/W |
| CIT art. 9 ust. 1e | JPK_CIT (JPK_KR_PD(1) v1-1) | ZOiS7 ze słownikiem znaczników MF, znaczniki PD, dziennik; RPD K_1–K_8 z rocznego uzgodnienia CIT (różnice trwałe i przejściowe, art. 18f, odpisy i rezerwy, amortyzacja, korekty lat ubiegłych); zapis zablokowany, gdy RPD nie uzgadnia się z dochodem; JPK_ST_KR — poza programem | Z/W |
| CIT art. 27 | zeznanie CIT-8, CIT-8/O | zestawienie kwot z uzgodnienia wyniku, zaliczek i odliczeń | Z/W |
| CIT art. 26 ust. 3 | IFT-2R / CIT-10Z | zestawienie wypłat na rzecz nierezydentów (ocena WHT — użytkownik) | Cz |
| PIT art. 8, 27, 30c, 44, 45 | dochód wspólników / właściciela, PIT-36, PIT-36L | podział dochodu wg udziałów, skala / liniowy, odliczenie składek | Z/W |
| PIT art. 31–42, ustawa o sus | płatnik: zaliczki, ZUS, składka zdrowotna | listy płac (umowa o pracę, zlecenie, dzieło, powołanie), limit 30-krotności, próg 120 000 zł, PIT-2, ulga dla młodych, ryczałt do 200 zł; DRA/RCA, PIT-4R, PIT-8AR, PIT-11 | Z/W |
| UoR art. 24 ust. 5 | bieżące prowadzenie ksiąg (przyjęty termin ujęcia operacji miesiąca: 15. dzień następnego miesiąca — do potwierdzenia w polityce) | asystent i kalendarz terminów | Z/W |
| VAT art. 86a, 88 ust. 1 pkt 4 | ograniczenia odliczenia (samochody osobowe, noclegi, gastronomia) | kategorie wydatków z procentem odliczenia | Z/W |
| VAT art. 89b, CIT art. 18f ust. 1 pkt 2 | korekta przy niezapłaconych zobowiązaniach (90 dni) | asystent: błąd dla zobowiązań > 90 dni po terminie; CIT: automatyczne zwiększenie podstawy w okresie, w którym upłynął 90. dzień (także zaliczki) — tylko transakcje handlowe (kontrahent z NIP), tylko część zaliczona do KUP wg dekretu (NKUP i VAT odliczony pomijane), osobno dla źródła „inne” i „zyski kapitałowe”; historia korekt per zobowiązanie zapisywana z zaliczką; zmniejszenie po zapłacie tylko do wysokości zapisanych zwiększeń; zakupy na kontach aktywów sygnalizowane do oceny | Z/W |
| VAT art. 96b, CIT art. 15d, Ordynacja art. 117ba | biała lista, płatności > 15 000 zł | sprawdzenie rachunku w wykazie, asystent: gotówka > 15 000 zł | Z/W |
| VAT art. 108a | mechanizm podzielonej płatności | asystent: wskazówka dla faktur > 15 000 zł bez MPP (załącznik nr 15 — ocenia użytkownik) | Cz |
| VAT art. 31a, UoR art. 30, CIT art. 15a | kurs NBP z dnia poprzedzającego | pobieranie kursu NBP, asystent: dokument walutowy bez tabeli | Z/W |
| CIT art. 16 ust. 1 pkt 28, 51 | reprezentacja, 75% kosztów samochodu osobowego | część NKUP pozycji w dekrecie i w uzgodnieniu CIT | Z/W |
| CIT art. 19, 4a pkt 10, 7b | stawki 9% / 19% | 9% tylko dla małego podatnika / pierwszego roku, bez wyłączeń z ust. 1b–1c i gdy przychody bieżącego roku (narastająco) ≤ 2 mln euro (rok ≠ 12 mies.: 1/12 za każdy pełny miesiąc) przeliczone po kursie NBP z pierwszego dnia roboczego roku podatkowego, zaokr. do 1000 zł po przeliczeniu; brak kursu — wynik wstępny, zaliczki nie można zapisać (kurs pobierany z NBP); status małego podatnika wymaga potwierdzenia kompletnej sprzedaży poprzedniego roku, a faktury zaksięgowane służą kontroli pomocniczej; kurs z 1. dnia roboczego października; po przekroczeniu limitu — 19% od dochodu narastająco; zyski kapitałowe odrębnym źródłem zawsze 19% | Z/W |
| KSH art. 300^3, UoR art. 36 | kapitał akcyjny P.S.A. | objęcie akcji za wkład pieniężny nie tworzy kapitału (ewidencja wkładów do wniesienia poza bilansem); kapitał akcyjny/zapasowy uznawany przy wpłacie, proporcjonalnie do objęcia; objęcia zaksięgowane według wcześniejszej logiki (kapitał przy objęciu) wykrywa asystent — wpłaty najpierw rozliczają ich należne wkłady, korekta stornem (gdy nic nie wpłacono) albo PK wg podręcznika | Z |
| VAT — JPK_V7 | sprzedaż poza krajem (art. 100), MPP, wersja struktury | K_12 wykazywane również w K_11; MPP nie jest oznaczane w JPK_V7(2)/(3); wariant 2 do okresu 01.2026, wariant 3 od 02.2026 (także korekty) | Z |
| CIT art. 25 | zaliczki | narastająco, miesięczne/kwartalne, terminy | Z/W |
| CIT art. 16a–16m | amortyzacja podatkowa | odrębne parametry podatkowe | Z/W |
| CIT art. 1 | podmiotowość | blokada CIT dla spółek transparentnych i osób fizycznych | Z |
| Ordynacja art. 63 | zaokrąglenia do pełnych złotych | deklaracja VAT, CIT | Z |
| KSH art. 191–195, 344, 347–349, 300^15 | ograniczenia wypłat na rzecz właścicieli | wymagane potwierdzenie przed księgowaniem wypłaty | Z/W |
| KSH (wpis do KRS) | zmiany kapitału | wymagana sygnatura wpisu dla form rejestrowych | Z/W |
| RODO art. 5, 17, 25, 32 | minimalizacja, ochrona danych, prawo do usunięcia | szyfrowanie bazy (SQLCipher), załączników (AES-256-GCM, klucz w magazynie sekretów systemu, wykrywanie modyfikacji; pliki z wcześniejszych wersji szyfrowane przy otwarciu) i kopii; odtworzenie zapisuje bazę wyłącznie zaszyfrowaną po pełnej weryfikacji (HMAC stron, integrity_check, schemat, liczby wierszy, skrót dziennika), TLS, role, brak telemetrii; żądanie usunięcia: odmowa z podstawą (art. 17 ust. 3 lit. b; art. 74 UoR, art. 70/86 OP, art. 94 pkt 9b KP) w okresie przechowywania, anonimizacja po jego upływie (z załącznikami), wstrzymanie usuwania (kontrola, spór, bieg przedawnienia), opcjonalne wygaszenie starszych kopii lokalnych po nowej kopii; zapis w dzienniku | Z/W |
| CIT art. 7 ust. 5, 15c | straty z lat ubiegłych, limit kosztów finansowania dłużnego | straty: 5 kolejnych lat podatkowych z łańcucha lat w programie; lata spoza programu — koniec okresu potwierdzany przez użytkownika (bez szacowania); 50% albo jednorazowo do 5 mln zł, odrębnie dla źródeł, także w zaliczkach. Art. 15c: kwalifikacja kont (koszt finansowania / przychód odsetkowy / wyłączenie; domyślnie wg RZiS), limit 3 mln zł albo 250 000 zł za każdy rozpoczęty miesiąc roku ≠ 12 mies. (ust. 1a), 30% podatkowej EBITDA ze wszystkich źródeł; nadwyżka wyłączana automatycznie (K_5) i dzielona między źródła proporcjonalnie do kosztów finansowania; koszty finansowania skapitalizowane w wartości początkowej (konta bilansowe, np. 080) nie wchodzą do limitu w roku poniesienia, wyłączenia — oznaczenie konta „nie dotyczy”; rejestr nadwyżek (ust. 3): nadwyżka z zamkniętego roku lub z CIT-8 sprzed programu, odrębnie dla źródeł, zaliczana w rozliczeniu rocznym tylko w 5 kolejnych latach podatkowych i tylko w wolnym limicie roku (RPD K_6); termin odliczenia straty zależny od lat jeszcze nieutworzonych oznaczony jako prognozowany | Z/W |
| UoR art. 37 ust. 4 i 6 | podatek odroczony | okresy odwrócenia = kolejne lata podatkowe od dnia bilansowego (także niekalendarzowe); środki trwałe — odwrócenie istniejącej różnicy z planów przyszłych odpisów bilansowych i podatkowych (metoda degresywna, ulepszenia; nowe różnice po dniu bilansowym nie są odwróceniem); prognoza stawki na rok podatkowy z uzasadnieniem; lata nieutworzone — 12 miesięcy (art. 8 ust. 1 CIT, bez kopiowania długości krótkiego/wydłużonego roku), oznaczane jako prognoza do potwierdzenia kalendarza; część różnicy ŚT nieodwracana planami — w roku zbycia/likwidacji lub wg udokumentowanej decyzji, inaczej „do weryfikacji” (bez przypisywania do ostatniego roku); księgowanie zablokowane, gdy podatek oparty na niezatwierdzonych prognozach przekracza próg istotności z polityki (domyślnie 0 zł); zyski kapitałowe 19%; aktywo ze strat wyłącznie z rejestru strat, po udokumentowanej ocenie i w okresie odliczenia | Z/W |
| JPK_KR_PD — RPD | podział różnic | korekty ręczne z rodzajem różnicy (trwała → K_1/K_4, przejściowa → K_2/K_5; lata ubiegłe → K_3/K_6); test poszczególnych pól | Z/W |

Pozycje **Z/W** i **Cz** wymagają sprawdzenia przez księgowego, doradcę podatkowego lub prawnika przed
wykorzystaniem wyników w rozliczeniach. Odpowiedzialność za rozliczenia ponosi użytkownik (§ 6 licencji).

## Przegląd zgodności — 30.10.2026

Sprawdzono parametry i mechanizmy wymagane przepisami (stan prawny znany na dzień przeglądu; pozycje „do potwierdzenia”
wymagają sprawdzenia w aktualnym tekście aktu przed pierwszym rozliczeniem).

| Obszar | Wynik | Działanie |
|---|---|---|
| UoR art. 14, 23–24 — trwałość zapisów, ciągłość, zamknięte okresy | zgodne: zapisu zaksięgowanego nie można usunąć ani zmienić (tylko storno/korekta), łańcuch sum kontrolnych, blokada zamkniętych okresów | — |
| UoR art. 10 — polityka, plan kont, opis systemu | zgodne: zatwierdzenie polityki i planu kont blokuje księgowanie do czasu akceptacji; raport „Opis systemu” | dołączyć wydruk do dokumentacji |
| Płace 2026 — płaca minimalna 4806 zł, limit 30-krotności 282 600 zł, stawki ZUS, FP 2,45%, FGŚP 0,10%, PIT 12/32%, próg 120 000 zł, PIT-2 300 zł, KUP 250/300 zł, ulga dla młodych 85 528 zł | zgodne | — |
| Płace — parametry innych lat | **poprawiono**: dodano 2025 (4666 zł, 260 190 zł); dla roku bez zweryfikowanych parametrów program odmawia wyliczenia zamiast liczyć stawkami 2026 | aktualizacja programu co rok |
| KP art. 92 — wynagrodzenie chorobowe | **poprawiono**: limit 33 dni w roku (14 dni od roku następującego po ukończeniu 50 lat); powyżej — informacja o zasiłku ZUS (Z-3) | — |
| CIT art. 15d / Ordynacja art. 117ba — przelewy > 15 000 zł na rachunek spoza białej listy | **poprawiono**: wynik sprawdzenia rachunku zapamiętywany przy kontrahencie (data, wynik, identyfikator zapytania); asystent ostrzega o fakturach > 15 000 zł bez aktualnego sprawdzenia | sprawdzać w dniu zlecenia przelewu |
| CIT art. 18f, VAT art. 89a/89b — należności i zobowiązania po 90 dniach | ostrzeżenia asystenta; CIT 18f dla zobowiązań wyliczany po udokumentowanej kwalifikacji, wyłącznie dla jawnego KUP; ręczne korekty powiązane ze zobowiązaniem wykluczają automat, ulgę na złe długi po stronie należności i korekty VAT wprowadza użytkownik | — |
| P.S.A. — kapitał akcyjny, test wypłacalności (art. 300^15 KSH), uchwały (zarząd / rada dyrektorów) | zgodne: nazwa kapitału, potwierdzenie testu wypłacalności przy wypłatach, projekty uchwał | rejestr akcjonariuszy prowadzi podmiot uprawniony (poza programem) |
| JPK_CIT — zakres i termin | harmonogram w asystencie: od lat podatkowych 2025 — PGK i podatnicy > 50 mln euro (oznaczenie w sekcji CIT), od 2026 — podatnicy z JPK_V7M, od 2027 — z JPK_V7K i pozostali; termin do końca 7. miesiąca po roku | potwierdzać zmiany terminów w komunikatach MF |
| KSeF — terminy obowiązku, sankcje | do potwierdzenia (reguły w rejestrze przepisów) | — |
| RODO | dane osobowe tylko w bazie lokalnej / własnym serwerze, szyfrowane kopie, brak telemetrii, dziennik zdarzeń | rejestr czynności przetwarzania prowadzi spółka |
