namespace PelnaKsiegowosc.Domain.Legal;

public enum RuleStatus
{
    /// <summary>Sprawdzone w źródłach na dzień weryfikacji.</summary>
    Zweryfikowana,
    /// <summary>Wymaga potwierdzenia (rozbieżne źródła lub projekt zmian).</summary>
    DoPotwierdzenia
}

/// <summary>Reguła prawna stosowana przez program — wartość, podstawa, okres obowiązywania i stan weryfikacji.</summary>
public sealed record LegalRule(
    string Code,
    string Area,
    string Name,
    string Value,
    string LegalBasis,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    RuleStatus Status,
    string? Note = null);

/// <summary>Akt prawny monitorowany pod kątem zmian (identyfikator ELI Dziennika Ustaw).</summary>
public sealed record MonitoredAct(string EliId, string Name, string Area);

/// <summary>
/// Rejestr przepisów, na których opiera się program. Każda reguła ma podstawę prawną i stan weryfikacji.
/// Przy zmianie przepisów aktualizuje się rejestr (nowa wersja) i kod, który z niego korzysta; monitor zmian
/// (ELI API Sejmu) sygnalizuje akty zmieniające opublikowane po dacie weryfikacji.
/// </summary>
public static class LegalRules
{
    public const string Version = "PRZEPISY-2026.10";
    /// <summary>Dzień, na który zweryfikowano rejestr z dostępnymi źródłami.</summary>
    public static readonly DateOnly VerifiedOn = new(2026, 10, 30);

    private static readonly DateOnly Y2026 = new(2026, 1, 1);

    public static readonly IReadOnlyList<LegalRule> All = new List<LegalRule>
    {
        // VAT
        new("VAT_STAWKI", "VAT", "Stawki VAT", "23%, 8%, 5%, 0%, zw, np, oo", "art. 41, 43, 146ef ustawy o VAT", new(2011, 1, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_JPK_TERMIN", "VAT", "Termin JPK_V7M/V7K i zapłaty VAT", "25. dzień następnego miesiąca (deklaracja V7K — za kwartał)", "art. 99 ust. 1–2, art. 103 ust. 1 ustawy o VAT; art. 12 § 5 Ordynacji", new(2020, 10, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_SAMOCHOD", "VAT", "Odliczenie VAT — samochód osobowy w użytku mieszanym", "50%", "art. 86a ust. 1 ustawy o VAT", new(2014, 4, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_NOCLEGI", "VAT", "Brak odliczenia VAT — usługi noclegowe i gastronomiczne", "0%", "art. 88 ust. 1 pkt 4 lit. b ustawy o VAT", new(2004, 5, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_MPP", "VAT", "Mechanizm podzielonej płatności", "faktury > 15 000 zł brutto z towarami/usługami z załącznika nr 15", "art. 108a ust. 1a ustawy o VAT", new(2019, 11, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_89B", "VAT", "Korekta VAT naliczonego przy nieuregulowanej fakturze", "90 dni od terminu płatności", "art. 89b ustawy o VAT", new(2013, 1, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_BIALA_LISTA", "VAT", "Płatność na rachunek z białej listy", "transakcje > 15 000 zł; zawiadomienie ZAW-NR w 7 dni", "art. 96b ustawy o VAT; art. 117ba Ordynacji; art. 15d ust. 1 pkt 3 ustawy o CIT", new(2020, 1, 1), null, RuleStatus.Zweryfikowana),
        new("VAT_KURS", "VAT", "Kurs przeliczenia waluty", "średni kurs NBP z ostatniego dnia roboczego przed powstaniem obowiązku podatkowego", "art. 31a ust. 1 ustawy o VAT", new(2014, 1, 1), null, RuleStatus.Zweryfikowana),
        // KSeF
        new("KSEF_DUZI", "KSeF", "Obowiązek KSeF — najwięksi podatnicy", "od 1.02.2026 (sprzedaż w 2024 r. > 200 mln zł)", "przepisy przejściowe nowelizacji ustawy o VAT wprowadzającej KSeF", new(2026, 2, 1), null, RuleStatus.Zweryfikowana),
        new("KSEF_POZOSTALI", "KSeF", "Obowiązek KSeF — pozostali podatnicy", "od 1.04.2026", "przepisy przejściowe nowelizacji ustawy o VAT wprowadzającej KSeF", new(2026, 4, 1), null, RuleStatus.Zweryfikowana),
        new("KSEF_MIKRO", "KSeF", "Okres przejściowy KSeF — sprzedaż do 10 000 zł miesięcznie i faktury z kas", "do 31.12.2026", "przepisy przejściowe nowelizacji ustawy o VAT wprowadzającej KSeF", Y2026, new DateOnly(2026, 12, 31), RuleStatus.Zweryfikowana),
        new("KSEF_KARY", "KSeF", "Sankcje za wystawianie faktur poza KSeF", "od 1.01.2027 (projekt MF zakłada przesunięcie)", "przepisy o sankcjach za niestosowanie KSeF (ustawa o VAT)", new(2027, 1, 1), null, RuleStatus.DoPotwierdzenia,
            "We wrześniu 2026 r. MF zapowiedziało projekt przesunięcia sankcji — sprawdź stan legislacji."),
        // CIT
        new("CIT_STAWKI", "CIT", "Stawki CIT", "19%; 9% dla małego podatnika i w roku rozpoczęcia działalności (bez zysków kapitałowych)", "art. 19 ust. 1 ustawy o CIT", new(2019, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_ZALICZKA", "CIT", "Termin zaliczki CIT", "20. dzień następnego miesiąca (kwartalnie — po kwartale)", "art. 25 ust. 1, 1b ustawy o CIT", new(2019, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_8", "CIT", "Termin zeznania CIT-8 i zapłaty podatku", "koniec 3. miesiąca roku następnego", "art. 27 ust. 1 ustawy o CIT", new(2019, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_SAMOCHOD_75", "CIT", "Koszty samochodu osobowego w użytku mieszanym", "75% wydatków eksploatacyjnych (bez składek OC)", "art. 16 ust. 1 pkt 51, ust. 5f ustawy o CIT", new(2019, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_LIMIT_AUTA", "CIT", "Limit amortyzacji/leasingu samochodu osobowego", "225 000 zł (elektryczny, wodorowy); 150 000 zł (emisja < 50 g CO2/km); 100 000 zł (pozostałe)", "art. 16 ust. 1 pkt 4, 49, 49a ustawy o CIT", Y2026, null, RuleStatus.Zweryfikowana,
            "Dla aut nabytych przed 2026 r. stosuje się dotychczasowe limity amortyzacji; leasing — nowe limity także dla starszych umów (stanowisko KIS)."),
        new("CIT_GOTOWKA", "CIT", "Płatności gotówkowe ponad limit", "> 15 000 zł — wyłączenie z KUP", "art. 15d ustawy o CIT; art. 19 Prawa przedsiębiorców", new(2017, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_REPREZENTACJA", "CIT", "Reprezentacja", "nie jest KUP", "art. 16 ust. 1 pkt 28 ustawy o CIT", new(1993, 1, 1), null, RuleStatus.Zweryfikowana),
        new("CIT_NISKOCENNE", "CIT", "Jednorazowa amortyzacja niskocennych składników", "do 10 000 zł netto", "art. 16d ust. 1 ustawy o CIT", new(2018, 1, 1), null, RuleStatus.Zweryfikowana),
        new("JPK_CIT", "CIT", "JPK_CIT (JPK_KR_PD) — termin", "koniec 7. miesiąca po roku podatkowym; za 2026 r. — podatnicy składający JPK_V7; JPK_ST_KR — zwolnienie za pierwszy rok", "art. 9 ustawy o CIT; rozporządzenie MFiG (Dz.U. 2026 poz. 188)", Y2026, null, RuleStatus.DoPotwierdzenia,
            "Termin i zakres pierwszego obowiązku zmieniano w 2026 r. — potwierdź w komunikatach MF."),
        // Rachunkowość
        new("UOR_SPRAWOZDANIE", "Rachunkowość", "Sporządzenie sprawozdania finansowego", "3 miesiące od dnia bilansowego", "art. 52 ust. 1 UoR", new(1995, 1, 1), null, RuleStatus.Zweryfikowana),
        new("UOR_ZATWIERDZENIE", "Rachunkowość", "Zatwierdzenie sprawozdania i złożenie w KRS", "6 miesięcy od dnia bilansowego; KRS — 15 dni od zatwierdzenia", "art. 53 ust. 1, art. 69 UoR", new(1995, 1, 1), null, RuleStatus.Zweryfikowana),
        new("UOR_BIEZACE", "Rachunkowość", "Bieżące prowadzenie ksiąg", "operacje miesiąca ujmowane do 15. dnia następnego miesiąca (praktyka; potwierdź w polityce)", "art. 24 ust. 5 UoR", new(1995, 1, 1), null, RuleStatus.DoPotwierdzenia),
        new("UOR_PRZECHOWYWANIE", "Rachunkowość", "Przechowywanie ksiąg", "5 lat od początku roku następnego; zatwierdzone sprawozdania — trwale", "art. 74 UoR", new(1995, 1, 1), null, RuleStatus.Zweryfikowana),
        // Płace
        new("PLACA_MINIMALNA", "Płace", "Minimalne wynagrodzenie", "4806 zł brutto", "rozporządzenie RM z 2025 r. w sprawie minimalnego wynagrodzenia na 2026 r.", Y2026, new DateOnly(2026, 12, 31), RuleStatus.Zweryfikowana),
        new("ZUS_LIMIT_30", "Płace", "Roczna podstawa składek emerytalnej i rentowych", "282 600 zł (30 × 9420 zł)", "art. 19 ust. 1 ustawy o systemie ubezpieczeń społecznych", Y2026, new DateOnly(2026, 12, 31), RuleStatus.Zweryfikowana),
        new("ZUS_STAWKI", "Płace", "Stawki składek ZUS", "emerytalna 9,76% + 9,76%; rentowa 1,5% + 6,5%; chorobowa 2,45%; wypadkowa 1,67% (do 9 osób); FP 2,45%; FGŚP 0,10%; zdrowotna 9%", "ustawa o systemie ubezpieczeń społecznych; ustawa o świadczeniach opieki zdrowotnej", Y2026, null, RuleStatus.Zweryfikowana),
        new("PIT_SKALA", "Płace", "Skala PIT", "12% do 120 000 zł, 32% nadwyżka; kwota zmniejszająca 300 zł/mies. (3600 zł rocznie)", "art. 27 ust. 1, art. 32 ustawy o PIT", new(2022, 7, 1), null, RuleStatus.Zweryfikowana),
        new("PIT_KOSZTY", "Płace", "Koszty uzyskania przychodu — umowa o pracę", "250 zł/mies. (300 zł dojeżdżający)", "art. 22 ust. 2 ustawy o PIT", new(2019, 10, 1), null, RuleStatus.Zweryfikowana),
        new("PIT_RYCZALT_200", "Płace", "Zryczałtowany PIT — umowy do 200 zł", "12%", "art. 30 ust. 1 pkt 5a ustawy o PIT", new(2022, 7, 1), null, RuleStatus.Zweryfikowana),
        new("ZUS_TERMIN", "Płace", "Termin składek ZUS (płatnik z osobowością prawną)", "15. dzień następnego miesiąca", "art. 47 ust. 1 ustawy o systemie ubezpieczeń społecznych", new(2023, 1, 1), null, RuleStatus.Zweryfikowana),
        new("PIT_TERMIN", "Płace", "Termin wpłaty zaliczek PIT pobranych przez płatnika", "20. dzień następnego miesiąca; PIT-11 i PIT-4R do końca stycznia", "art. 38, 39, 42 ustawy o PIT", new(2019, 1, 1), null, RuleStatus.Zweryfikowana),
    };

    /// <summary>Akty monitorowane pod kątem zmian.</summary>
    public static readonly IReadOnlyList<MonitoredAct> MonitoredActs = new List<MonitoredAct>
    {
        new("DU/1994/591", "Ustawa o rachunkowości", "Rachunkowość"),
        new("DU/2004/535", "Ustawa o podatku od towarów i usług", "VAT"),
        new("DU/1992/86", "Ustawa o podatku dochodowym od osób prawnych", "CIT"),
        new("DU/1991/350", "Ustawa o podatku dochodowym od osób fizycznych", "PIT"),
        new("DU/1997/926", "Ordynacja podatkowa", "Podatki"),
        new("DU/1998/887", "Ustawa o systemie ubezpieczeń społecznych", "Płace"),
        new("DU/2000/1037", "Kodeks spółek handlowych", "Spółki"),
        new("DU/2018/646", "Prawo przedsiębiorców", "Działalność"),
    };

    public static LegalRule Get(string code) => All.First(r => r.Code == code);
}
