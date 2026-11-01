# Rozliczenie pracy przy 30 dniach choroby w miesiącu 31-dniowym

Wersja: 1.0.0. Dotyczy issue #6, nie zastępuje ustalenia prawa do świadczenia ani podstawy chorobowego (issues #3 i #5).

1. W Kadrach zapisz daty niezdolności do pracy na podstawie dokumentacji. Wymagane jest dokładnie 30 dni choroby w miesiącu 31-dniowym.
2. Na roboczej liście wybierz pracownika, otwórz ewidencję czasu pracy i przygotuj projekt grafiku. Projekt proponuje standardowe dni robocze i wymiar etatu, ale **nie zakłada przepracowanych godzin**.
3. Uzgodnij wszystkie dni i planowane godziny z zatwierdzonym harmonogramem. Wpisz faktyczne godziny pracy pozostałego dnia. Dzień wolny ma 0 planowanych i przepracowanych godzin. Nieodpłatna nieobecność musi mieć odpowiedni zapis w Kadrach i zgodną liczbę dni na liście.
4. Podaj odnośnik do grafiku i ewidencji czasu pracy, potwierdź sprawdzenie i przelicz listę. Do księgowania potrzebne są również kompletna podstawa chorobowego i ustalenie art. 92 KP.

Wynagrodzenie za faktyczną pracę w tym szczególnym przypadku = miesięczna pensja / suma godzin grafiku × faktyczne godziny pracy; zaokrąglenie dopiero kwoty do groszy. Przykład: 6000 / 176 × 8 = 272,73 zł. Wynagrodzenie to wchodzi do podstawy składek; świadczenie chorobowe jest liczone osobno według zweryfikowanej podstawy.

Potwierdzenie obejmuje konkretną listę, pozycję, miesiąc, kwotę bazową, wymiar etatu, zatrudnienie, grafik i źródłowe nieobecności. Zmiana danych unieważnia potwierdzenie i blokuje księgowanie do ponownej weryfikacji. Po zaksięgowaniu zmiana wymaga storna i nowej listy. Odciski wartości dziesiętnych są niezależne od reprezentacji SQLite/PostgreSQL.

Cały miesiąc choroby, w tym luty 28/29 dni, pozostaje rozliczany jako brak pracy. Rozliczenia płatnego urlopu w pozostałym dniu, nadgodzin i zatrudnienia tylko przez część miesiąca wymagają odrębnej ścieżki; formularz 30/31 nie przyjmuje ich jako zwykłej pracy lub dnia wolnego.

Podstawa: art. 80 KP, § 4 i § 11 rozporządzenia MPiPS z 29.05.1996, stanowisko GIP GPP-87-4560-64/09/PE/RP. Stanowisko GIP potwierdza konieczność zapłaty za rzeczywiście wykonaną pracę: https://g.infor.pl/p/_wspolne/pliki/736000/736464-stanowisko-glownego-inspektoratu-pracy-w-sprawie-sposobu-ustalania-wynagrodzenia-za-jeden-dzien-wykonywania-pracy.pdf . Testy techniczne nie są niezależną opinią kadrowo-płacową.
