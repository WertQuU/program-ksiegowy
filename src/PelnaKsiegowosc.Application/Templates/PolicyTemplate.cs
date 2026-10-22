namespace PelnaKsiegowosc.Application.Templates;

/// <summary>
/// Szablon dokumentacji zasad (polityki) rachunkowości wg art. 10 ust. 1 UoR. Zawiera pola [DO UZUPEŁNIENIA] —
/// aplikacja blokuje księgowanie do czasu ich wypełnienia i zatwierdzenia polityki przez kierownika jednostki.
/// Szablon NIE stanowi porady prawnej ani gotowej polityki — musi zostać dostosowany przez księgowego.
/// </summary>
public static class PolicyTemplate
{
    public static string Build(string companyName, string legalFormName, string managementBody, string fiscalYearDescription, string chartName, string softwareVersion) => $$"""
# Zasady (polityka) rachunkowości

**Jednostka:** {{companyName}} ({{legalFormName}})
**Podstawa:** art. 10 ustawy z dnia 29 września 1994 r. o rachunkowości (UoR)
**Obowiązuje od:** [DO UZUPEŁNIENIA]
**Przyjęta zarządzeniem / uchwałą:** [DO UZUPEŁNIENIA — kierownik jednostki: {{managementBody}}]

## 1. Rok obrotowy i okresy sprawozdawcze (art. 10 ust. 1 pkt 1)

1. Rok obrotowy: {{fiscalYearDescription}}.
2. Okresem sprawozdawczym jest miesiąc kalendarzowy. Zestawienie obrotów i sald sporządza się na koniec każdego miesiąca (art. 18 ust. 1 UoR).
3. Księgi rachunkowe prowadzone są w siedzibie jednostki / przez biuro rachunkowe: [DO UZUPEŁNIENIA].

## 2. Metody wyceny aktywów i pasywów oraz ustalania wyniku finansowego (art. 10 ust. 1 pkt 2)

1. Wynik finansowy ustala się w wariancie porównawczym rachunku zysków i strat.
2. Koszty ewidencjonuje się w układzie rodzajowym (konta zespołu 4).
3. Środki trwałe i WNiP wycenia się według cen nabycia lub kosztu wytworzenia pomniejszonych o odpisy amortyzacyjne i aktualizujące (art. 28 ust. 1 pkt 1 UoR).
4. Odpisy amortyzacyjne dokonuje się metodą liniową od miesiąca następującego po miesiącu przyjęcia do używania, z zastosowaniem stawek podatkowych (art. 32 ust. 6 UoR), chyba że dla danego składnika ustalono inaczej: [DO UZUPEŁNIENIA].
5. Składniki o wartości początkowej nieprzekraczającej [DO UZUPEŁNIENIA] zł odpisuje się jednorazowo w koszty w miesiącu przyjęcia do używania.
6. Należności wycenia się w kwocie wymaganej zapłaty z zachowaniem zasady ostrożności; odpisy aktualizujące tworzy się dla należności przeterminowanych powyżej [DO UZUPEŁNIENIA] dni.
7. Operacje w walutach obcych wycenia się po kursie średnim NBP z dnia poprzedzającego dzień operacji (art. 30 ust. 2 UoR); na dzień bilansowy — po kursie średnim NBP z tego dnia.
8. Zapasy: [DO UZUPEŁNIENIA — metoda ewidencji i wyceny rozchodu, np. FIFO].
9. Rozliczenia międzyokresowe kosztów: odpisuje się w koszty, gdy kwota jednostkowa przekracza [DO UZUPEŁNIENIA] zł; w pozostałych przypadkach — jednorazowo.
10. Rezerwy na zobowiązania tworzy się zgodnie z art. 35d UoR.
11. Podatek odroczony: [DO UZUPEŁNIENIA — czy jednostka korzysta ze zwolnienia dla jednostek małych/mikro].
12. Próg istotności: [DO UZUPEŁNIENIA].

## 3. Sposób prowadzenia ksiąg rachunkowych (art. 10 ust. 1 pkt 3)

1. Zakładowy plan kont: „{{chartName}}” — wykaz kont, zasady klasyfikacji zdarzeń, zasady prowadzenia kont ksiąg pomocniczych oraz ich powiązania z kontami księgi głównej określa załącznik nr 1 (wydruk planu kont z aplikacji).
2. Księgi pomocnicze rozrachunków prowadzi się w podziale na kontrahentów (wymiar „kontrahent” pozycji dekretu); środków trwałych — w ewidencji środków trwałych aplikacji.
3. Dzienniki częściowe: SPR (sprzedaż), ZAK (zakupy), BANK, KASA, PK (polecenia księgowania), AM (amortyzacja), KAP (zdarzenia kapitałowe), BO (bilans otwarcia), ZAM (zamknięcie roku). Zapisy numerowane są w sposób ciągły w roku obrotowym.
4. Korekty zapisów zaksięgowanych wykonuje się wyłącznie zapisami korygującymi (storno czerwone), zgodnie z art. 25 ust. 1 pkt 2 UoR.
5. Wykaz zbiorów danych tworzących księgi rachunkowe na informatycznych nośnikach danych: baza danych aplikacji (dziennik, księga główna, księgi pomocnicze, zestawienia obrotów i sald, wykaz składników aktywów i pasywów) oraz archiwa kopii zapasowych.

### Opis systemu informatycznego (art. 10 ust. 1 pkt 3 lit. c)

- Program: Pełna Księgowość, wersja {{softwareVersion}}; data rozpoczęcia eksploatacji: [DO UZUPEŁNIENIA].
- Algorytmy i parametry: opisane w dokumentacji programu (ARCHITECTURE.md, ACCOUNTING_REQUIREMENTS.md) oraz w raporcie „Opis systemu informatycznego” generowanym przez program.
- Ochrona danych: dostęp przez konto użytkownika systemu operacyjnego (oraz konto serwera PostgreSQL w trybie serwerowym), dziennik audytowy z łańcuchem skrótów, blokada zmian zapisów zaksięgowanych, szyfrowane kopie zapasowe.
- Zmiany wersji programu: rejestrowane w dzienniku audytowym (wersja aplikacji i schematu bazy).

## 4. Ochrona danych (art. 10 ust. 1 pkt 4, art. 71–76 UoR)

1. Dostęp do programu mają wyłącznie osoby upoważnione: [DO UZUPEŁNIENIA — lista osób i ról].
2. Kopie zapasowe wykonuje się [DO UZUPEŁNIENIA — częstotliwość], przechowuje: [DO UZUPEŁNIENIA — lokalizacje], w postaci zaszyfrowanej.
3. Księgi rachunkowe przechowuje się przez co najmniej 5 lat od początku roku następującego po roku obrotowym, którego dane dotyczą; zatwierdzone sprawozdania finansowe — trwale (art. 74 UoR). Dokumentację podatkową — do upływu terminu przedawnienia zobowiązania podatkowego.
4. Klucz szyfrowania kopii zapasowych przechowuje: [DO UZUPEŁNIENIA] (utrata klucza oznacza nieodwracalną utratę możliwości odtworzenia zaszyfrowanych kopii).

## 5. Inwentaryzacja (art. 26–27 UoR)

Terminy i metody inwentaryzacji: [DO UZUPEŁNIENIA].

## 6. Zatwierdzenie

Kierownik jednostki: [DO UZUPEŁNIENIA — imię, nazwisko, funkcja]
Data: [DO UZUPEŁNIENIA]
""";
}
