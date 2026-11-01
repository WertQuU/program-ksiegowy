using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Ledger;

namespace PelnaKsiegowosc.Tests.DomainRules;

public class ProtectedYearEntriesTests
{
    [Theory]
    [InlineData(EntryKind.BilansOtwarcia)]
    [InlineData(EntryKind.Zamkniecie)]
    public void System_year_entries_cannot_be_reversed_by_standard_correction(EntryKind kind)
    {
        var posted = new JournalEntry
        {
            CompanyId = Guid.NewGuid(),
            FiscalYearId = Guid.NewGuid(),
            PeriodId = Guid.NewGuid(),
            Kind = kind,
            Status = EntryStatus.Zaksiegowany,
            PostingDate = new DateOnly(2026, 12, 31),
            DocumentNumber = "PK/SYSTEM"
        };

        var error = Assert.Throws<DomainException>(() => EntryValidator.BuildReversal(
            posted, CorrectionMethod.StornoCzerwone, new DateOnly(2027, 1, 15),
            Guid.NewGuid(), Guid.NewGuid(), "Próba ręcznego odwrócenia księgowania systemowego",
            Guid.NewGuid(), DateTime.UtcNow));
        Assert.Contains("nie można stornować", error.Message);
        Assert.Null(posted.ReversedByEntryId);
    }

    [Fact]
    public void Ordinary_posted_entry_remains_reversible_with_provenance()
    {
        var posted = new JournalEntry
        {
            CompanyId = Guid.NewGuid(),
            FiscalYearId = Guid.NewGuid(),
            PeriodId = Guid.NewGuid(),
            Kind = EntryKind.Zwykly,
            Status = EntryStatus.Zaksiegowany,
            PostingDate = new DateOnly(2026, 3, 10),
            DocumentNumber = "PK/NORMAL",
            Lines = new List<JournalLine>
            {
                new() { LineNo = 1, AccountNumber = "130-01", Debit = 100m },
                new() { LineNo = 2, AccountNumber = "801", Credit = 100m }
            }
        };
        var reversal = EntryValidator.BuildReversal(posted, CorrectionMethod.StornoCzerwone,
            new DateOnly(2026, 3, 12), Guid.NewGuid(), posted.FiscalYearId,
            "Zwykłe storno poprawnej transakcji", Guid.NewGuid(), DateTime.UtcNow);
        Assert.Equal(EntryKind.Storno, reversal.Kind);
        Assert.Equal(posted.Id, reversal.ReversesEntryId);
        Assert.Equal(-100m, reversal.Lines.Single(x => x.LineNo == 1).Debit);
        Assert.Equal(-100m, reversal.Lines.Single(x => x.LineNo == 2).Credit);
    }
}
