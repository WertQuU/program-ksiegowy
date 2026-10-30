# Wymagania księgowe i ich realizacja

Dokument opisuje, jak program realizuje wymagania prowadzenia ksiąg rachunkowych. Program **wspiera** spełnienie
wymagań — za prowadzenie ksiąg odpowiada kierownik jednostki (art. 4 ust. 5 UoR), a za rozliczenia — podatnik.
Odwołania do przepisów mają charakter informacyjny i wymagają weryfikacji z aktualnym brzmieniem
([COMPLIANCE_MATRIX.md](COMPLIANCE_MATRIX.md)). Skróty: UoR — ustawa z 29.09.1994 o rachunkowości;
VAT — ustawa z 11.03.2004 o podatku od towarów i usług; CIT — ustawa z 15.02.1992 o podatku dochodowym od osób prawnych.

## 1. Jednostka i forma prawna

- Obsługiwane formy: sp. z o.o., P.S.A., S.A., S.K.A., sp.k., sp.j., sp.p., s.c., działalność osoby fizycznej
  (księgi rachunkowe), fundacja, stowarzyszenie, spółdzielnia, inna.
- Forma decyduje o: wymaganym oznaczeniu w firmie (ostrzeżenie), obowiązku wpisu do KRS, statusie podatnika CIT
  (spółki osobowe transparentne i osoby fizyczne — PIT wspólników/właściciela), nazwie kapitału podstawowego
  (kapitał zakładowy / akcyjny / wspólników / fundusz udziałowy / założycielski / statutowy / kapitał właściciela),
  określeniu właścicieli i organu będącego kierownikiem jednostki, zasadach wypłat na rzecz właścicieli.
- Waluta ksiąg: PLN (art. 9 ust. 1 UoR). Dane jednostki walidowane (NIP, REGON, KRS, kod pocztowy, IBAN).

## 2. Polityka rachunkowości (art. 10 UoR)

Szablon dokumentu z polami `[DO UZUPEŁNIENIA…]` dopasowany do formy prawnej i roku obrotowego. Księgowanie jest
zablokowane, dopóki polityka nie zostanie uzupełniona i zatwierdzona przez kierownika jednostki; zmiana
zatwierdzonej polityki tworzy nową, niezatwierdzoną wersję. Parametry polityki używane przez program: metoda
korekt (storno czerwone/czarne), próg środków niskocennych, jednostka mała/mikro.

## 3. Rok obrotowy i okresy (art. 3 ust. 1 pkt 9–10, art. 12 UoR)

- Rok 12 miesięcy; pierwszy rok krótszy lub do 24 miesięcy. Okresy sprawozdawcze miesięczne.
- Zamknięcie miesiąca po liście kontrolnej (bufor, dokumenty niezaksięgowane, uzgodnienie VAT z kontami,
  saldo kasy, bank, amortyzacja, zaliczki CIT); ostrzeżenia wymagają uzasadnienia. Ponowne otwarcie — z uzasadnieniem
  w historii i dzienniku zdarzeń.
- Zamknięcie roku nieodwracalne, potwierdzane wpisaniem frazy; przeksięgowanie wyniku, utworzenie roku następnego
  z planem kont i bilansem otwarcia (BO = salda zamknięcia, z przeniesieniem pozycji rozrachunkowych).

## 4. Księgi rachunkowe (art. 13–18 UoR)

| Wymaganie | Realizacja |
|---|---|
| Dziennik, ciągła numeracja w roku | numer pozycji nadawany przy zaksięgowaniu, bez luk; dzienniki częściowe (SPR, ZAK, BANK, KASA, PK, AM, KAP, BO, ZAM) |
| Księga główna, podwójny zapis | każdy zapis bilansuje się (Wn = Ma), konta z planu kont roku |
| Księgi pomocnicze | analityka kont, wymiar „kontrahent”, rozrachunki per kontrahent, ewidencja środków trwałych, rejestry VAT |
| Zestawienie obrotów i sald | raport ZOiS (BO, obroty okresu i narastająco, salda), kontrola zgodności z dziennikiem |
| Wykaz składników aktywów i pasywów | zestawienie sald kont pomocniczych, bilans |
| Zakładowy plan kont | szablon (wariant porównawczy), wersjonowany per rok, zatwierdzany; konta z mapowaniem na pozycje bilansu/RZiS i klasyfikacją CIT |

## 5. Dowody i zapisy (art. 20–25 UoR)

- Dowód zawiera: rodzaj i numer, strony, opis i wartość, datę operacji i sporządzenia, akceptację (zatwierdzenie),
  dekretację; historia operacji na dokumencie.
- Zapis zawiera: datę operacji, rodzaj i numer dowodu z datą, opis, kwotę, datę zapisu, konta, numer pozycji
  i operatora (art. 23 ust. 2–3).
- Zapis zaksięgowany jest trwały (blokada w programie i w bazie). Korekta: storno zgodne z polityką + zapis
  korygujący (art. 25). Dokument zaksięgowany zmienia się dokumentem korygującym.
- Komputerowe księgi (art. 24 ust. 4–5): łańcuch skrótów SHA-256 zapisów i dziennika zdarzeń, kontrola
  integralności, dziennik zmian, opis systemu informatycznego (raport).

## 6. Rozrachunki, bank i kasa

Pozycje rozrachunkowe z wiekowaniem; dopasowanie płatności z wyciągów (MT940, CSV) z propozycjami; kompensaty
z notą; uzgodnienie salda rachunku z wyciągiem; kasa — operacje KP/KW z kontrolą salda nieujemnego. Różnice
kursowe zrealizowane przy rozliczeniu walutowym.

## 7. Środki trwałe i WNiP

Ewidencja z odrębną amortyzacją bilansową i podatkową (liniowa, degresywna z przejściem na liniową, jednorazowa,
brak), umorzenie początkowe, ulepszenia, likwidacja i sprzedaż, część odpisów niestanowiąca KUP; naliczenie
miesięczne z zapisem w dzienniku AM.

## 8. Kapitały i właściciele

Zdarzenia: objęcie udziałów/akcji (wkład pieniężny i niepieniężny), wpłata wkładu, podwyższenie i obniżenie
kapitału podstawowego, podział zysku, pokrycie straty, kapitał rezerwowy, dywidenda/udział w zysku i zaliczki,
wypłata z podatkiem pobranym przez płatnika, pożyczki od właścicieli. Księgowanie dopiero po potwierdzeniu
wymaganych dokumentów (uchwała, umowa, wpis do KRS, potwierdzenie dopuszczalności wypłaty właściwe dla formy).
Moduł nie zastępuje księgi udziałów ani rejestru akcjonariuszy.

## 9. VAT

Ewidencja sprzedaży i zakupów (art. 109 ust. 3 VAT) z mapowaniem na pola JPK_V7 (K_xx), wersjonowane reguły;
odliczenie VAT wymaga świadomego potwierdzenia zakresu i okresu (art. 86 ust. 10b, 11); import usług
z samonaliczeniem; wyliczenie deklaracji w pełnych złotych; rozliczenie na kontach; plik JPK_V7M.

### Kategorie wydatków

Pozycja zakupu może mieć kategorię (np. paliwo do samochodu osobowego w użytku mieszanym), która wyznacza konto,
procent odliczenia VAT i część kosztu niestanowiącą KUP. Dekret dzieli koszt (netto + VAT nieodliczony) na część
KUP i NKUP; część NKUP jest oznaczona w pozycji zapisu i uwzględniana w uzgodnieniu CIT (także po stornie).

## 10. Faktury i KSeF

Faktury FA(3) z elementami art. 106e VAT (w zakresie obsługiwanym przez generator), walidowane z oficjalnym XSD;
wysyłka do KSeF i zapis numeru KSeF (oznaczenie w JPK); pobieranie faktur zakupowych jako dokumentów roboczych.
Faktury z numerem KSeF nie mogą być anulowane — tylko korygowane.

## 11. CIT

Klasyfikacja przychodów i kosztów wg kont (KUP/NKUP/do analizy), korekty manualne z podstawą prawną,
amortyzacja podatkowa zamiast bilansowej, zaliczki narastająco (miesięczne lub kwartalne, stawka 9% lub 19%,
zaokrąglenia do pełnych złotych, terminy z przesunięciem na dzień roboczy), uzgodnienie wyniku bilansowego
z podatkowym. Pozycje „do analizy” wymagają potwierdzenia przy zatwierdzaniu zaliczki. Dla jednostek
niebędących podatnikami CIT moduł jest zablokowany (pozostaje uzgodnienie pomocnicze).

## 12. Sprawozdanie finansowe

Bilans i RZiS (wariant porównawczy) wg załącznika nr 1 UoR z danymi porównawczymi; kontrola aktywa = pasywa;
konta niezmapowane sygnalizowane. Plik XML sprawozdania (struktura JednostkaInna) bez informacji dodatkowej
i zestawienia zmian w kapitale — wymaga uzupełnienia i podpisów w innym narzędziu.

## 13. Ochrona i przechowywanie danych (art. 71–76 UoR)

Szyfrowane kopie zapasowe weryfikowane po zapisie, kopie automatyczne, przechowywanie zdalne (Object Storage),
odtwarzanie do nowej lokalizacji, dane nieusuwane przy odinstalowaniu programu, okresy przechowywania pokazywane
w archiwum. Dziennik zdarzeń nie zawiera haseł ani kluczy.

## 14. Nieobsługiwane

Inwentaryzacja (arkusze spisowe, różnice), magazyn i rozliczanie zapasów, wariant kalkulacyjny RZiS,
informacja dodatkowa, rachunek przepływów pieniężnych, konsolidacja, PIT właścicieli, JPK_V7K, JPK_KR(_PD),
podpis XAdES w KSeF, płace.
