using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace Hoard.Desktop.ViewModels;

public sealed class DashboardAccount
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Bank/organization the account belongs to (falls back to the institution name).</summary>
    public string Bank { get; init; } = "";
    public decimal Balance { get; init; }
    public bool IsExcluded { get; init; }
    public string BalanceDate { get; init; } = "";
}

public sealed class DashboardTypeCard
{
    public long? TypeId { get; init; }
    public string Name { get; init; } = "";
    public AccountKind Kind { get; init; }
    public string? Color { get; init; }
    public decimal Total { get; init; }
    public int AccountCount { get; init; }
    public string Subtitle { get; init; } = "";
    public double ShareOfAssets { get; init; }
    public IReadOnlyList<DashboardAccount> Accounts { get; init; } = [];
}

/// <summary>Home page: net-worth KPIs, a card per account type, and a horizontal bar chart of type totals.</summary>
/// <summary>Dashboard owner filter: 0 = everyone, -1 = accounts without an owner.</summary>
public sealed record OwnerFilterOption(long Id, string Label)
{
    public static readonly OwnerFilterOption Everyone = new(0, "Everyone");
    public static readonly OwnerFilterOption Unassigned = new(ReportingService.UnassignedOwner, "(no owner)");
    public override string ToString() => Label;
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ReportingService _reports;
    private readonly IInstitutionRepository _institutions;
    private readonly IHouseholdRepository _household;
    private readonly ChartTheme _chartTheme;

    public DashboardViewModel(ReportingService reports, IInstitutionRepository institutions, IHouseholdRepository household, ThemeService theme)
    {
        _reports = reports;
        _household = household;
        _institutions = institutions;
        _chartTheme = new ChartTheme(theme);
        theme.Changed += () => { if (Cards.Count > 0) _ = LoadAsync(); };
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private decimal _netWorth;
    [ObservableProperty] private decimal _assets;
    [ObservableProperty] private decimal _liabilities;
    [ObservableProperty] private int _accountCount;
    [ObservableProperty] private int _excludedCount;
    [ObservableProperty] private string _lastSyncText = "";
    [ObservableProperty] private string _asOfText = "";
    [ObservableProperty] private ISeries[] _typeSeries = [];
    [ObservableProperty] private Axis[] _typeXAxes = [];
    [ObservableProperty] private Axis[] _typeYAxes = [];
    [ObservableProperty] private double _typeChartHeight = 320;

    public ObservableCollection<DashboardTypeCard> Cards { get; } = new();

    /// <summary>Owner filter: everyone, each household member, or accounts with no owner.</summary>
    public ObservableCollection<OwnerFilterOption> OwnerOptions { get; } = new() { OwnerFilterOption.Everyone };
    [ObservableProperty] private OwnerFilterOption _selectedOwner = OwnerFilterOption.Everyone;
    private bool _rebuildingOwners;
    partial void OnSelectedOwnerChanged(OwnerFilterOption value) { if (value is not null && !_rebuildingOwners) _ = LoadAsync(); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _rebuildingOwners = true;
            try
            {
                var keep = SelectedOwner?.Id ?? 0;
                OwnerOptions.Clear();
                OwnerOptions.Add(OwnerFilterOption.Everyone);
                foreach (var m in await _household.ListAsync()) OwnerOptions.Add(new OwnerFilterOption(m.Id, m.Name));
                OwnerOptions.Add(OwnerFilterOption.Unassigned);
                _selectedOwner = OwnerOptions.FirstOrDefault(o => o.Id == keep) ?? OwnerFilterOption.Everyone;
                OnPropertyChanged(nameof(SelectedOwner));
            }
            finally { _rebuildingOwners = false; }
            long? ownerId = SelectedOwner.Id switch { 0 => null, var id => id };

            var report = await _reports.BalancesByTypeAsync(includeEmptyTypes: true, ownerId: ownerId);
            NetWorth = report.NetWorth;
            Assets = report.Assets;
            Liabilities = report.Liabilities;
            AccountCount = report.AccountCount;
            ExcludedCount = report.ExcludedCount;
            AsOfText = (report.LatestBalanceUtc is { } d ? $"Balances as of {d.ToLocalTime():g}" : "No balances yet — run a sync.") +
                       (ownerId is null ? "" : $" · showing {SelectedOwner.Label}'s accounts");

            var institutions = await _institutions.ListAsync();
            var lastSync = institutions.Where(i => i.LastSyncAtUtc is not null).Select(i => i.LastSyncAtUtc!.Value).DefaultIfEmpty().Max();
            var failing = institutions.Count(i => i.LastSyncStatus == SyncStatus.Failed);
            LastSyncText = lastSync == default ? "Never synced" : $"Last sync {lastSync.ToLocalTime():g}" + (failing > 0 ? $" · {failing} institution(s) failing" : "");

            Cards.Clear();
            var assetTotal = Math.Max(1m, report.Assets);
            foreach (var g in report.Groups)
            {
                var included = g.Accounts.Count(a => !a.ExcludeFromNetWorth);
                Cards.Add(new DashboardTypeCard
                {
                    TypeId = g.TypeId,
                    Name = g.Name,
                    Kind = g.Kind,
                    Color = g.Color,
                    Total = g.Total,
                    AccountCount = g.AccountCount,
                    ShareOfAssets = g.Kind == AccountKind.Asset ? (double)(g.Total / assetTotal) : 0,
                    Subtitle = $"{included} account{(included == 1 ? "" : "s")}" +
                               (g.AccountCount > included ? $" · {g.AccountCount - included} excluded" : "") +
                               (g.Kind == AccountKind.Asset && report.Assets > 0 ? $" · {g.Total / assetTotal:P0} of assets" : ""),
                    Accounts = g.Accounts.Select(a => new DashboardAccount
                    {
                        Id = a.AccountId,
                        Name = a.Name,
                        Bank = a.BankName,
                        Balance = a.Balance,
                        IsExcluded = a.ExcludeFromNetWorth,
                        BalanceDate = a.BalanceDateUtc is { } bd ? bd.ToLocalTime().ToString("MMM d") : "",
                    }).ToList(),
                });
            }

            // Every defined type on the axis (zero bars included), tallest first within the configured order.
            var groups = report.Groups.ToList();
            TypeChartHeight = ChartTheme.BarChartHeight(groups.Count);
            TypeYAxes = [_chartTheme.CategoryAxis(groups.Select(g => g.Name).ToArray(), everyLabel: true)];
            TypeXAxes = [_chartTheme.CurrencyAxis()];
            TypeSeries = ChartTheme.TypeBars(groups, _chartTheme);
            Status = "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
