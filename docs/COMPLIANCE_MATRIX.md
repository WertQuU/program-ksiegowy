# Macierz zgodności z przepisami

Statusy: **Z** — zaimplementowane i objęte testami automatycznymi; **Z/W** — zaimplementowane, wymaga weryfikacji
merytorycznej (księgowej, podatkowej lub prawnej) albo testu na rzeczywistych danych/systemie; **Cz** — częściowo;
**N** — nieobsługiwane. Program nie deklaruje zgodności z przepisami — macierz wskazuje, czego dotyczy
implementacja i co wymaga sprawdzenia przez uprawnioną osobę. Brzmienie przepisów należy zweryfikować na dzień użycia.

| Przepis | Wymaganie (skrót) | Realizacja w programie | Status |
|---|---|---|---|
| UoR art. 4a | odpowiedzialność kierownika jednostki | wskazanie kierownika w kreatorze; zatwierdzanie polityki | Z |
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
| UoR art. 26–27 | inwentaryzacja | brak modułu inwentaryzacji | N |
| UoR art. 28–35 | wycena aktywów i pasywów | ŚT (amortyzacja), waluty, rozrachunki; brak odpisów aktualizujących należności, rezerw automatycznych | Cz |
| UoR art. 32 | amortyzacja bilansowa | metody liniowa, degresywna, jednorazowa | Z/W |
| UoR art. 45–46, zał. nr 1 | bilans, RZiS | wariant porównawczy z danymi porównawczymi | Z/W |
| UoR art. 45 ust. 1f | sprawozdanie w strukturze logicznej | XML bez informacji dodatkowej, bez walidacji XSD (schemat niedołączony) | Cz |
| UoR art. 71–76 | ochrona i przechowywanie | szyfrowane kopie, weryfikacja, retencja, okresy przechowywania | Z/W |
| VAT art. 106e | elementy faktury | generator FA(3) w zakresie typowych faktur krajowych i korekt | Z/W |
| VAT art. 106ga i n. | KSeF | wysyłka i pobieranie (token), walidacja XSD FA(3); test na atrapie API | Z/W |
| VAT art. 86 | prawo i termin odliczenia | wymagane potwierdzenie zakresu i okresu, kontrola terminów | Z/W |
| VAT art. 109 ust. 3 | ewidencja VAT | rejestry z polami JPK | Z/W |
| Rozporządzenie MF ws. JPK_V7 | struktura JPK_V7M | generator; mapowanie pól wymaga weryfikacji; bez walidacji XSD | Cz |
| JPK_V7K, osoba fizyczna | kwartalny wariant, sekcja OsobaFizyczna | — | N |
| UoR art. 24 ust. 5 | bieżące prowadzenie ksiąg (przyjęty termin ujęcia operacji miesiąca: 15. dzień następnego miesiąca — do potwierdzenia w polityce) | asystent i kalendarz terminów | Z/W |
| VAT art. 86a, 88 ust. 1 pkt 4 | ograniczenia odliczenia (samochody osobowe, noclegi, gastronomia) | kategorie wydatków z procentem odliczenia | Z/W |
| VAT art. 89b, CIT art. 15b | korekta przy niezapłaconych zobowiązaniach (90 dni) | asystent: błąd dla zobowiązań > 90 dni po terminie | Z/W |
| VAT art. 96b, CIT art. 15d, Ordynacja art. 117ba | biała lista, płatności > 15 000 zł | sprawdzenie rachunku w wykazie, asystent: gotówka > 15 000 zł | Z/W |
| VAT art. 108a | mechanizm podzielonej płatności | asystent: wskazówka dla faktur > 15 000 zł bez MPP (załącznik nr 15 — ocenia użytkownik) | Cz |
| VAT art. 31a, UoR art. 30, CIT art. 15a | kurs NBP z dnia poprzedzającego | pobieranie kursu NBP, asystent: dokument walutowy bez tabeli | Z/W |
| CIT art. 16 ust. 1 pkt 28, 51 | reprezentacja, 75% kosztów samochodu osobowego | część NKUP pozycji w dekrecie i w uzgodnieniu CIT | Z/W |
| CIT art. 19 | stawki 9% / 19% | stawka wg statusu małego podatnika i pierwszego roku | Z/W |
| CIT art. 25 | zaliczki | narastająco, miesięczne/kwartalne, terminy | Z/W |
| CIT art. 16a–16m | amortyzacja podatkowa | odrębne parametry podatkowe | Z/W |
| CIT art. 1 | podmiotowość | blokada CIT dla spółek transparentnych i osób fizycznych | Z |
| Ordynacja art. 63 | zaokrąglenia do pełnych złotych | deklaracja VAT, CIT | Z |
| KSH art. 191–195, 344, 347–349, 300^15 | ograniczenia wypłat na rzecz właścicieli | wymagane potwierdzenie przed księgowaniem wypłaty | Z/W |
| KSH (wpis do KRS) | zmiany kapitału | wymagana sygnatura wpisu dla form rejestrowych | Z/W |
| RODO art. 5, 25, 32 | minimalizacja, ochrona danych | dane kontrahentów ograniczone, szyfrowane kopie, brak telemetrii | Z/W |

Pozycje **Z/W** i **Cz** wymagają sprawdzenia przez księgowego, doradcę podatkowego lub prawnika przed
wykorzystaniem wyników w rozliczeniach. Odpowiedzialność za rozliczenia ponosi użytkownik (§ 6 licencji).
