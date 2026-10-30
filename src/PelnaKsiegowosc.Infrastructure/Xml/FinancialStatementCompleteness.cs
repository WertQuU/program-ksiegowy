using PelnaKsiegowosc.Application.Reports;

namespace PelnaKsiegowosc.Infrastructure.Xml;

/// <summary>
/// Kontrola kompletności sprawozdania finansowego przed oznaczeniem pliku jako gotowego do podpisu (art. 45, 48–48b, 52 UoR).
/// Zgodność ze schematem XSD nie oznacza kompletności — brak informacji dodatkowej, pola „do uzupełnienia”, konta bez
/// pozycji sprawozdania albo niezamknięty rok blokują wersję gotową (dostępna wyłącznie wersja robocza).
/// </summary>
public static class FinancialStatementCompleteness
{
    public static List<string> Check(StatementValues balanceSheet, FinancialStatementXmlBuilder.Result xml, IEnumerable<string> notesTexts,
        string? goingConcern, string? policies, bool yearClosed, bool auditRequired = false)
    {
        var missing = new List<string>();
        if (!yearClosed) missing.Add("Rok obrotowy nie jest zamknięty — sprawozdanie sporządza się na dzień zamknięcia ksiąg (art. 45 ust. 1, art. 52 ust. 1 UoR).");
        if (!xml.Validation.IsValid) missing.Add($"Plik nie przechodzi walidacji schematem XSD ({xml.Validation.Errors.Count} błędów).");
        // podpisy składa się poza programem (aplikacja e-Sprawozdania / e-KRS); pominięcia dozwolone dla jednostek małych/mikro nie są brakiem
        missing.AddRange(xml.NotImplemented.Where(n => n.StartsWith("Dodatkowe informacje") || (auditRequired && n.StartsWith("Rachunek przepływów")))
            .Select(n => "Brak elementu: " + n));
        if (balanceSheet.Unmapped.Count > 0)
            missing.Add($"{balanceSheet.Unmapped.Count} kont z saldem bez pozycji bilansu — salda nie trafiają do sprawozdania.");
        missing.AddRange(balanceSheet.Warnings.Where(w => w.Contains("aktyw", StringComparison.OrdinalIgnoreCase) && w.Contains("pasyw", StringComparison.OrdinalIgnoreCase))
            .Select(w => "Bilans: " + w));
        if (notesTexts.Any(t => t.Contains("[DO UZUPEŁNIENIA", StringComparison.OrdinalIgnoreCase) || t.Contains("do uzupełnienia", StringComparison.OrdinalIgnoreCase)))
            missing.Add("Informacja dodatkowa / wprowadzenie zawiera pola „DO UZUPEŁNIENIA” (Raporty → „Teksty informacji dodatkowej”).");
        if (string.IsNullOrWhiteSpace(goingConcern)) missing.Add("Brak oświadczenia o kontynuacji działalności (wprowadzenie do sprawozdania).");
        if (string.IsNullOrWhiteSpace(policies)) missing.Add("Brak opisu przyjętych zasad (polityki) rachunkowości we wprowadzeniu.");
        return missing.Distinct().ToList();
    }
}
