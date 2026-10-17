using PsaKsiegi.Domain.Accounts;
using PsaKsiegi.Domain.Common;
using PsaKsiegi.Domain.Periods;

namespace PsaKsiegi.Domain.Ledger;

/// <summary>
/// Reguły poprawności dekretu — czyste, testowalne jednostkowo (bez dostępu do bazy).
/// </summary>
public static class EntryValidator
{
    public sealed record Context(
        FiscalYear FiscalYear,
        AccountingPeriod Period,
        IReadOnlyDictionary<Guid, Account> Accounts,
        bool AllowClosedYearForOpeningBalance = false);

    public static ValidationResult ValidateForPosting(JournalEntry entry, Context ctx)
    {
        var r = ValidateStructure(entry, ctx.Accounts);

        if (ctx.FiscalYear.Id != entry.FiscalYearId) r.Error("Zapis nie należy do wskazanego roku obrotowego.");
        if (ctx.FiscalYear.Status != FiscalYearStatus.Otwarty && !(ctx.AllowClosedYearForOpeningBalance && entry.Kind == EntryKind.BilansOtwarcia))
        {
            if (!(ctx.FiscalYear.Status == FiscalYearStatus.WZamknieciu && entry.Kind == EntryKind.Zamkniecie))
                r.Error($"Rok obrotowy {ctx.FiscalYear.Name} nie jest otwarty — księgowanie niedozwolone.");
        }
        if (ctx.Period.Id != entry.PeriodId) r.Error("Zapis nie należy do wskazanego okresu.");
        var closingInProgress = entry.Kind == EntryKind.Zamkniecie && ctx.FiscalYear.Status == FiscalYearStatus.WZamknieciu;
        if (ctx.Period.Status != PeriodStatus.Otwarty && !closingInProgress) r.Error($"Okres {ctx.Period.Name} jest zamknięty — księgowanie niedozwolone.");
        if (!ctx.Period.Contains(entry.PostingDate))
            r.Error($"Data księgowania {entry.PostingDate.ToPl()} nie mieści się w okresie {ctx.Period.Name}.");
        if (!ctx.FiscalYear.Contains(entry.PostingDate))
            r.Error("Data księgowania jest poza rokiem obrotowym.");

        // Zdarzenie powinno być ujęte w okresie, w którym nastąpiło (art. 20 ust. 1 UoR).
        if (entry.Kind == EntryKind.Zwykly && entry.OperationDate > ctx.Period.EndDate)
            r.Error($"Data operacji {entry.OperationDate.ToPl()} jest późniejsza niż koniec okresu {ctx.Period.Name} — zdarzenie należy ująć w okresie, w którym nastąpiło.");
        if (entry.Kind == EntryKind.Zwykly && entry.OperationDate < ctx.FiscalYear.StartDate)
            r.Warning($"Data operacji {entry.OperationDate.ToPl()} poprzedza rok obrotowy — sprawdź, czy zdarzenie nie dotyczy poprzedniego roku.");
        if (entry.Kind == EntryKind.Zwykly && entry.OperationDate < ctx.Period.StartDate)
            r.Warning($"Data operacji jest wcześniejsza niż okres księgowania {ctx.Period.Name}.");
        return r;
    }

    public static ValidationResult ValidateStructure(JournalEntry entry, IReadOnlyDictionary<Guid, Account> accounts)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(entry.Description)) r.Error("Opis operacji jest wymagany (art. 23 ust. 2 pkt 3 UoR).");
        if (string.IsNullOrWhiteSpace(entry.DocumentNumber)) r.Error("Numer dowodu księgowego jest wymagany (art. 23 ust. 2 pkt 2 UoR).");
        if (string.IsNullOrWhiteSpace(entry.DocumentType)) r.Error("Rodzaj dowodu księgowego jest wymagany.");
        if (entry.Lines.Count < 2 && entry.Kind != EntryKind.BilansOtwarcia)
            r.Error("Dekret musi zawierać co najmniej dwie pozycje (zasada podwójnego zapisu).");
        if (entry.Lines.Count == 0) r.Error("Dekret nie zawiera pozycji.");
        if (entry.Currency != "PLN" && (entry.ExchangeRate is null or <= 0))
            r.Error("Dla operacji w walucie obcej wymagany jest kurs waluty.");

        var parents = accounts.Values.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        var lineNos = new HashSet<int>();
        decimal balanceDebit = 0, balanceCredit = 0;
        foreach (var l in entry.Lines)
        {
            var label = $"Pozycja {l.LineNo}";
            if (!lineNos.Add(l.LineNo)) r.Error($"{label}: zduplikowany numer pozycji.");
            if (l.Debit != 0 && l.Credit != 0) r.Error($"{label}: kwota może wystąpić tylko po jednej stronie (Wn albo Ma).");
            if (l.Debit == 0 && l.Credit == 0) r.Error($"{label}: kwota nie może być zerowa.");
            if (Money.HasMoreThanTwoDecimals(l.Debit) || Money.HasMoreThanTwoDecimals(l.Credit))
                r.Error($"{label}: kwota może mieć najwyżej 2 miejsca po przecinku.");
            var negative = l.Debit < 0 || l.Credit < 0;
            if (negative && entry.Kind != EntryKind.Storno)
                r.Error($"{label}: kwoty ujemne (storno czerwone) dopuszczalne są wyłącznie w zapisach korygujących typu storno.");
            if (Math.Abs(l.Debit) > 999_999_999_999.99m || Math.Abs(l.Credit) > 999_999_999_999.99m)
                r.Error($"{label}: kwota przekracza dopuszczalny zakres.");

            if (!accounts.TryGetValue(l.AccountId, out var acc))
            {
                r.Error($"{label}: konto nie istnieje w planie kont roku.");
                continue;
            }
            if (!acc.IsActive || acc.IsBlocked) r.Error($"{label}: konto {acc.Number} jest nieaktywne lub zablokowane.");
            if (parents.Contains(acc.Id))
                r.Error($"{label}: konto {acc.Number} posiada konta analityczne — księgowanie dopuszczalne wyłącznie na najniższym poziomie analityki.");
            if ((acc.RequiresCounterparty || acc.IsSettlementAccount) && l.CounterpartyId is null)
                r.Error($"{label}: konto {acc.Number} wymaga wskazania kontrahenta.");
            if (entry.Currency != "PLN" && acc.Currency == entry.Currency && l.CurrencyAmount is null)
                r.Error($"{label}: konto walutowe {acc.Number} wymaga kwoty w walucie.");
            l.AccountNumber = acc.Number;

            if (acc.Type != AccountType.Pozabilansowe)
            {
                balanceDebit += l.Debit;
                balanceCredit += l.Credit;
            }
        }

        var offBalance = entry.Lines.Count(l => accounts.TryGetValue(l.AccountId, out var a) && a.Type == AccountType.Pozabilansowe);
        if (offBalance > 0 && offBalance != entry.Lines.Count)
            r.Error("Nie można łączyć kont pozabilansowych z bilansowymi w jednym dekrecie.");

        if (offBalance == 0 && balanceDebit != balanceCredit)
            r.Error($"Dekret nie bilansuje się: Wn {Money.Format(balanceDebit)} ≠ Ma {Money.Format(balanceCredit)} (różnica {Money.Format(balanceDebit - balanceCredit)}).");
        return r;
    }

    /// <summary>
    /// Buduje storno zapisu zaksięgowanego. Storno czerwone: te same strony, kwoty ujemne.
    /// Storno czarne: strony zamienione, kwoty dodatnie.
    /// </summary>
    public static JournalEntry BuildReversal(JournalEntry original, Companies.CorrectionMethod method, DateOnly postingDate,
        Guid periodId, Guid fiscalYearId, string reason, Guid userId, DateTime nowUtc)
    {
        if (!original.IsPosted) throw new DomainException("Stornować można wyłącznie zapis zaksięgowany. Zapis w buforze można edytować.");
        if (original.ReversedByEntryId is not null) throw new DomainException("Zapis został już wystornowany.");
        if (original.Kind == EntryKind.Storno) throw new DomainException("Nie można stornować zapisu stornującego — wprowadź zapis korygujący.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Podaj przyczynę korekty.");

        var rev = new JournalEntry
        {
            CompanyId = original.CompanyId,
            FiscalYearId = fiscalYearId,
            PeriodId = periodId,
            JournalCode = original.JournalCode,
            Kind = EntryKind.Storno,
            OperationDate = original.OperationDate,
            DocumentDate = original.DocumentDate,
            PostingDate = postingDate,
            Description = $"STORNO: {original.Description}",
            DocumentType = original.DocumentType,
            DocumentNumber = original.DocumentNumber,
            SourceDocumentId = original.SourceDocumentId,
            Currency = original.Currency,
            ExchangeRate = original.ExchangeRate,
            ExchangeRateTable = original.ExchangeRateTable,
            ReversesEntryId = original.Id,
            CorrectionReason = reason,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc
        };
        foreach (var l in original.Lines.OrderBy(x => x.LineNo))
        {
            var nl = new JournalLine
            {
                EntryId = rev.Id,
                LineNo = l.LineNo,
                AccountId = l.AccountId,
                AccountNumber = l.AccountNumber,
                CounterpartyId = l.CounterpartyId,
                OpenItemId = l.OpenItemId,
                Description = l.Description
            };
            if (method == Companies.CorrectionMethod.StornoCzerwone)
            {
                nl.Debit = -l.Debit;
                nl.Credit = -l.Credit;
                nl.CurrencyAmount = -l.CurrencyAmount;
            }
            else
            {
                nl.Debit = l.Credit;
                nl.Credit = l.Debit;
                nl.CurrencyAmount = l.CurrencyAmount;
            }
            rev.Lines.Add(nl);
        }
        return rev;
    }
}
