using CommunityToolkit.Mvvm.ComponentModel;
using PelnaKsiegowosc.Application.Setup;
using PelnaKsiegowosc.Application.Templates;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Presentation.ViewModels;

/// <summary>Pytania kreatora planu kont — w prostym języku.</summary>
public sealed partial class ChartOptionsForm : ObservableObject
{
    [ObservableProperty] private bool _detailedCosts = true;
    [ObservableProperty] private bool _tradingGoods = true;
    [ObservableProperty] private bool _employees;
    [ObservableProperty] private bool _foreignCurrencyEur;
    [ObservableProperty] private bool _foreignCurrencyUsd;
    [ObservableProperty] private bool _bankLoans;
    [ObservableProperty] private bool _leasing;

    public ChartOptions ToOptions() => new(DetailedCosts, TradingGoods, Employees, ForeignCurrencyEur, ForeignCurrencyUsd, BankLoans, Leasing);

    /// <summary>Przy szczegółowych kontach kosztów domyślnym kontem zakupu bez kategorii jest „Pozostałe usługi obce”.</summary>
    public async Task ApplyPostingSettingsAsync(KsiegiDbContext db, Guid companyId, IClock clock)
    {
        if (!DetailedCosts) return;
        var settings = new SettingsService(db, clock);
        var p = await settings.GetAsync(companyId, PostingSettings.SettingKey, () => new PostingSettings());
        p.PurchaseCostDefault = "402-99";
        await settings.SetAsync(companyId, PostingSettings.SettingKey, p);
        await db.SaveChangesAsync();
    }
}
