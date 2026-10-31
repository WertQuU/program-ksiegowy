using PelnaKsiegowosc.Application.Reports;
using PelnaKsiegowosc.Domain.Common;
using PelnaKsiegowosc.Domain.Companies;
using PelnaKsiegowosc.Domain.Documents;

namespace PelnaKsiegowosc.Infrastructure.Export;

/// <summary>
/// Papierowa wizualizacja danych dokumentu księgowego: statyczny PDF bez pól formularza.
/// Nie jest zamiennikiem oryginalnego dokumentu elektronicznego, FA(3), KSeF lub podpisu.
/// </summary>
public static class PaperArchivePdf
{
    public static byte[] Build(Document document, Company company, string operatorLogin,
        DateTime generatedUtc, IReadOnlyList<Attachment> attachments,
        IReadOnlyList<DocumentHistory> history, IReadOnlyDictionary<Guid, string> accounts)
    {
        var r = new ReportDocument
        {
            Title = "Wydruk archiwalny dowodu — " + Document.KindName(document.Kind) + " " + document.Number,
            CompanyName = company.FullName,
            CompanyNip = company.Nip,
            PeriodDescription = document.OperationDate.ToPl(),
            GeneratedAtUtc = generatedUtc,
            GeneratedBy = operatorLogin,
            Landscape = false
        };
        r.Columns.Add(new ReportColumn("Pole", ColumnKind.Text, 1.5));
        r.Columns.Add(new ReportColumn("Dane dokumentu", ColumnKind.Text, 4.5));
        void Line(string a, object? b) => r.Add(a, b?.ToString() ?? "");
        void Group(string name) => r.AddRow(new ReportRow(name, "") { IsHeader = true });

        Group("Identyfikacja");
        Line("Rodzaj", Document.KindName(document.Kind));
        Line("Numer wewnętrzny", document.Number);
        Line("Numer obcy", document.ExternalNumber);
        Line("Identyfikator", document.Id);
        Line("Status", document.Status);
        Line("Data wystawienia", document.IssueDate.ToPl());
        Line("Data operacji", document.OperationDate.ToPl());
        Line("Data wpływu", document.ReceiptDate?.ToPl());
        Line("Termin płatności", document.DueDate?.ToPl());
        Line("Opis", document.Description);
        Group("Strony");
        Line("Kontrahent", document.CounterpartyName);
        Line("NIP kontrahenta", document.CounterpartyNip);
        Line("Adres", document.CounterpartyAddress);
        Group("Kwoty i podatki");
        Line("Netto", Money.Format(document.NetAmount) + " " + document.Currency);
        Line("VAT", Money.Format(document.VatAmount) + " " + document.Currency);
        Line("Brutto", Money.Format(document.GrossAmount) + " " + document.Currency);
        Line("Brutto PLN", Money.Format(document.GrossAmountPln) + " PLN");
        Line("Okres VAT", document.VatPeriod?.ToPl());
        Line("Numer KSeF", document.KsefNumber);
        Line("KSeF — status", document.KsefStatus);
        Line("Numer zapisu księgowego", document.JournalEntryId);
        Line("Zatwierdzony", document.ApprovedAtUtc?.ToPlDateTime());
        Line("Zaksięgowany", document.PostedAtUtc?.ToPlDateTime());

        if (document.Lines.Count > 0)
        {
            Group("Pozycje");
            foreach (var x in document.Lines.OrderBy(x => x.LineNo))
                Line(x.LineNo + ". " + x.Name,
                    $"{x.Quantity:0.####} {x.Unit}; netto {Money.Format(x.NetAmount)}; VAT {x.VatRateCode}: {Money.Format(x.VatAmount)}; brutto {Money.Format(x.GrossAmount)}");
        }
        if (document.VatLines.Count > 0)
        {
            Group("Zestawienie VAT");
            foreach (var v in document.VatLines.OrderBy(v => v.VatRateCode))
                Line(v.VatRateCode, $"netto {Money.Format(v.NetAmountPln)} PLN; VAT {Money.Format(v.VatAmountPln)} PLN");
        }
        if (document.PostingLines.Count > 0)
        {
            Group("Dekretacja Wn / Ma");
            foreach (var l in document.PostingLines.OrderBy(l => l.LineNo))
                Line(accounts.GetValueOrDefault(l.AccountId, l.AccountId.ToString()),
                    $"Wn {Money.Format(l.Debit)}; Ma {Money.Format(l.Credit)}; {l.Description}");
        }
        if (attachments.Count > 0)
        {
            Group("Załączniki (wykaz, nie treść)");
            foreach (var a in attachments.OrderBy(a => a.FileName))
                Line(a.FileName, $"SHA-256: {a.Sha256}; {a.SizeBytes} B");
        }
        if (history.Count > 0)
        {
            Group("Historia dokumentu");
            foreach (var h in history.OrderBy(h => h.AtUtc))
                Line(h.AtUtc.ToPlDateTime() + " " + h.Action, h.Details);
        }

        r.Notes.Add("To wydruk archiwalny danych zapisanych w programie, nie oryginalna e-faktura ani dokument KSeF. Załączniki wymienione w wykazie wymagają odrębnej archiwizacji.");
        r.Notes.Add("PDF jest plikiem statycznym, ale nie jest technicznie niemożliwy do modyfikacji. Sprawdź sumę SHA-256 w indeksie eksportu.");
        return new PdfReportExporter().Export(r);
    }

    public static byte[] Index(Company company, string operatorLogin, DateTime generatedUtc,
        IReadOnlyList<(string File, string Sha256)> files)
    {
        var r = new ReportDocument
        {
            Title = "Indeks wydruków archiwalnych dokumentów księgowych",
            CompanyName = company.FullName, CompanyNip = company.Nip,
            PeriodDescription = "Cała baza dokumentów wskazanej jednostki",
            GeneratedAtUtc = generatedUtc, GeneratedBy = operatorLogin,
            Landscape = true
        };
        r.Columns.Add(new ReportColumn("Lp.", ColumnKind.Integer, 0.5));
        r.Columns.Add(new ReportColumn("Nazwa pliku", ColumnKind.Text, 3));
        r.Columns.Add(new ReportColumn("SHA-256", ColumnKind.Text, 4.5));
        var i = 0;
        foreach (var entry in files)
            r.Add(++i, entry.File, entry.Sha256);
        r.Notes.Add("Weryfikacja integralności: oblicz SHA-256 dla każdego pliku i porównaj z indeksem. Indeks sam też należy chronić przed zmianą.");
        r.Notes.Add("Nie zastępuje ewidencji, elektronicznych oryginałów, plików JPK, certyfikatów podpisu ani załączników.");
        return new PdfReportExporter().Export(r);
    }
}
