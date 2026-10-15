using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Categorization;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Import;
using Hoard.Core.Providers.RealEstate;
using Hoard.Core.Sync;

namespace Hoard.Desktop.ViewModels;

public sealed partial class InstitutionItem : ObservableObject
{
    public long Id { get; init; }
    public ProviderKind Provider { get; init; }
    public bool IsPlaid => Provider == ProviderKind.Plaid;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _lastError;

    public static InstitutionItem From(Institution i) => new()
    {
        Id = i.Id,
        Provider = i.Provider,
        Name = i.Name,
        StatusText = i.LastSyncStatus switch
        {
            SyncStatus.Never => "Never synced",
            SyncStatus.Running => "Syncing...",
            SyncStatus.Succeeded => $"Synced {i.LastSyncAtUtc?.ToLocalTime():g}",
            SyncStatus.PartialSuccess => $"Synced with warnings {i.LastSyncAtUtc?.ToLocalTime():g}",
            SyncStatus.Failed => "Failed",
            _ => "",
        },
        LastError = i.LastError,
    };
}

public sealed partial class AccountItem : ObservableObject
{
    private readonly IAccountRepository _accounts;

    public AccountItem(AccountSummary s, IAccountRepository accounts)
    {
        _accounts = accounts;
        Id = s.Id;
        InstitutionId = s.InstitutionId;
        InstitutionName = s.InstitutionName;
        _name = s.Name;
        BankName = s.BankName;
        DisplayName = s.DisplayName;
        OrgName = s.OrgName;
        TypeColor = s.CategoryColor;
        _excludeFromNetWorth = s.ExcludeFromNetWorth;
        _ownerId = s.OwnerId;
        Currency = s.Currency;
        Balance = s.Balance;
        AvailableBalance = s.AvailableBalance;
        BalanceDateLocal = s.BalanceDateUtc?.ToLocalTime();
        _categoryId = s.CategoryId;
        PossibleDuplicateOf = s.PossibleDuplicateOf;
    }

    public long Id { get; }
    public long InstitutionId { get; }
    /// <summary>Set when a sync flagged this row as a second copy of an existing account (see Accounts › Merge duplicates).</summary>
    public long? PossibleDuplicateOf { get; }
    public bool IsPossibleDuplicate => PossibleDuplicateOf is not null;
    public string InstitutionName { get; }
    public string BankName { get; }
    public string? DisplayName { get; private set; }
    public string? OrgName { get; }
    public string? TypeColor { get; }
    public string Currency { get; }
    public bool IsRenamed => !string.IsNullOrWhiteSpace(DisplayName);

    [ObservableProperty] private string _name;

    /// <summary>Toggled from the grid; persists immediately and asks the main view model to refresh totals.</summary>
    [ObservableProperty] private bool _excludeFromNetWorth;

    public Func<AccountItem, bool, Task>? ExcludeChanged { get; set; }

    partial void OnExcludeFromNetWorthChanged(bool value) => _ = ExcludeChanged?.Invoke(this, value);

    [ObservableProperty] private long? _ownerId;

    /// <summary>Combo-box friendly owner: 0 means no owner.</summary>
    public long OwnerIdOrZero
    {
        get => OwnerId ?? 0;
        set
        {
            var next = value == 0 ? null : (long?)value;
            if (next == OwnerId) return;
            OwnerId = next;
            OnPropertyChanged();
            _ = _accounts.SetOwnerAsync(Id, next);
            _ = OwnerChanged?.Invoke(this);
        }
    }

    public Func<AccountItem, Task>? OwnerChanged { get; set; }

    public void ApplyName(string effectiveName, string? displayName)
    {
        Name = effectiveName;
        DisplayName = displayName;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsRenamed));
    }
    public decimal Balance { get; }
    public decimal? AvailableBalance { get; }
    public DateTime? BalanceDateLocal { get; }

    [ObservableProperty] private long? _categoryId;

    /// <summary>Combo-box friendly view of <see cref="CategoryId"/>: 0 means "(uncategorized)".</summary>
    public long CategoryIdOrZero
    {
        get => CategoryId ?? 0;
        set
        {
            var next = value == 0 ? null : (long?)value;
            if (next == CategoryId) return;
            CategoryId = next;
            OnPropertyChanged();
            // Fire-and-forget persistence; the grid stays responsive.
            _ = _accounts.SetCategoryAsync(Id, next);
        }
    }
}

/// <summary>Account-type choice in the Transactions filter bar: 0 = all types, -1 = accounts without a type.</summary>
public sealed record TypeFilterOption(long Id, string Label)
{
    public const long AllId = 0;
    public const long UntypedId = -1;
    public static readonly TypeFilterOption All = new(AllId, "All account types");
    public static readonly TypeFilterOption Untyped = new(UntypedId, "(no type)");
    public override string ToString() => Label;
}

/// <summary>Account choice in the Transactions filter bar: Id 0 = every account (of the chosen type).</summary>
public sealed record AccountFilterOption(long Id, string Label, long? TypeId)
{
    public static readonly AccountFilterOption All = new(0, "All accounts", null);
    public override string ToString() => Label;
}

public enum AppPage { Dashboard, Institutions, Accounts, Transactions, Budget, Reports, Advisor, Settings }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IInstitutionRepository _institutions;
    private readonly IAccountRepository _accounts;
    private readonly ITransactionRepository _transactions;
    private readonly ICategoryRepository _categoryRepo;
    private readonly IHouseholdRepository _household;
    private readonly CategorizationService _categorization;
    private readonly SyncService _sync;
    private readonly InstitutionService _institutionService;
    private readonly PropertyService _propertyService;
    private readonly TransactionImportService _importService;
    private readonly AccountMergeService _merge;

    public MainViewModel(
        IInstitutionRepository institutions,
        IAccountRepository accounts,
        ITransactionRepository transactions,
        ICategoryRepository categoryRepo,
        IHouseholdRepository household,
        CategorizationService categorization,
        SyncService sync,
        InstitutionService institutionService,
        PropertyService propertyService,
        TransactionImportService importService,
        AccountMergeService merge,
        ReportsViewModel reports,
        DashboardViewModel dashboard,
        SettingsViewModel settings,
        BudgetViewModel budget,
        AdvisorViewModel advisor)
    {
        Reports = reports;
        Budget = budget;
        Advisor = advisor;
        Reports.ShowTransactions = ShowTransactionsAsync;
        Budget.OpenReport = () => { Reports.SelectedReportIndex = ReportsViewModel.BudgetTab; CurrentPage = AppPage.Reports; };
        Budget.TargetsChanged += () => ReportsNeedRefresh = true;
        Dashboard = dashboard;
        Settings = settings;
        _institutions = institutions;
        _accounts = accounts;
        _transactions = transactions;
        _categoryRepo = categoryRepo;
        _household = household;
        _categorization = categorization;
        _sync = sync;
        _institutionService = institutionService;
        _propertyService = propertyService;
        _importService = importService;
        _merge = merge;
        _filterCategory = FilterCategoryOptions[0]; // backing field: avoid triggering a load from the constructor
    }

    public InstitutionService InstitutionService => _institutionService;
    public PropertyService PropertyService => _propertyService;
    public TransactionImportService ImportService => _importService;
    public AccountMergeService MergeService => _merge;
    public ReportsViewModel Reports { get; }
    public BudgetViewModel Budget { get; }
    public AdvisorViewModel Advisor { get; }
    /// <summary>Set whenever data changed; the Budget page reloads on next activation.</summary>
    public bool BudgetNeedsRefresh { get; set; } = true;

    /// <summary>Accounts currently flagged as probable duplicates (a reconnect that re-issued provider ids).</summary>
    [ObservableProperty] private int _possibleDuplicateCount;
    public bool HasPossibleDuplicates => PossibleDuplicateCount > 0;
    public string DuplicateNotice => PossibleDuplicateCount == 1
        ? "A sync created an account that looks like a second copy of one you already had — usually a bank reconnected at SimpleFIN. Review it to merge or dismiss."
        : $"A sync created {PossibleDuplicateCount} accounts that look like second copies of ones you already had — usually a bank reconnected at SimpleFIN. Review them to merge or dismiss.";
    partial void OnPossibleDuplicateCountChanged(int value) { OnPropertyChanged(nameof(HasPossibleDuplicates)); OnPropertyChanged(nameof(DuplicateNotice)); }

    /// <summary>Raised after a desktop-initiated sync that flagged new probable duplicates, so the window can open the review dialog.</summary>
    public event EventHandler? DuplicatesDetected;
    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }
    /// <summary>Set whenever data changed (sync, categorization); the Reports page reloads on next activation.</summary>
    public bool ReportsNeedRefresh { get; set; } = true;
    public bool DashboardNeedsRefresh { get; set; } = true;

    [ObservableProperty] private AppPage _currentPage = AppPage.Dashboard;

    partial void OnCurrentPageChanged(AppPage value) => _ = OnPageActivatedAsync(value);

    private async Task OnPageActivatedAsync(AppPage page)
    {
        try
        {
            if (page == AppPage.Dashboard && DashboardNeedsRefresh) { DashboardNeedsRefresh = false; await Dashboard.LoadAsync(); }
            if (page == AppPage.Reports && ReportsNeedRefresh) { ReportsNeedRefresh = false; await Reports.LoadAllAsync(); }
            if (page == AppPage.Budget && BudgetNeedsRefresh) { BudgetNeedsRefresh = false; await Budget.LoadAsync(); }
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand] private void GoDashboard() => CurrentPage = AppPage.Dashboard;
    [RelayCommand] private void GoInstitutions() => CurrentPage = AppPage.Institutions;
    [RelayCommand] private void GoAccounts() => CurrentPage = AppPage.Accounts;
    [RelayCommand] private void GoTransactions() => CurrentPage = AppPage.Transactions;
    [RelayCommand] private void GoReports() => CurrentPage = AppPage.Reports;
    [RelayCommand] private void GoBudget() => CurrentPage = AppPage.Budget;
    [RelayCommand] private void GoAdvisor() => CurrentPage = AppPage.Advisor;

    /// <summary>
    /// Opens the Transactions page filtered to a category (a group also matches its sub-categories; null = every
    /// category) and a date range, clearing the other filters. Used by the reports' drill-throughs.
    /// </summary>
    public async Task ShowTransactionsAsync(long? categoryId, DateOnly from, DateOnly to)
    {
        _filterText = ""; OnPropertyChanged(nameof(FilterText));
        _filterFrom = from.ToDateTime(TimeOnly.MinValue); OnPropertyChanged(nameof(FilterFrom));
        _filterTo = to.ToDateTime(TimeOnly.MinValue); OnPropertyChanged(nameof(FilterTo));
        _filterCategory = FilterCategoryOptions.FirstOrDefault(o => o.Id == categoryId) ?? FilterCategoryOptions[0];
        OnPropertyChanged(nameof(FilterCategory));
        _filterType = TypeFilterOption.All; OnPropertyChanged(nameof(FilterType));
        RebuildAccountFilterOptions();
        PageIndex = 0;
        CurrentPage = AppPage.Transactions;
        await LoadTransactionsAsync();
    }
    [RelayCommand] private void GoSettings() => CurrentPage = AppPage.Settings;

    /// <summary>Jump to a specific report tab (0 net worth, 1 income vs expenses, 2 spending, 3 balances by type).</summary>
    [RelayCommand]
    private void GoReport(string index)
    {
        Reports.SelectedReportIndex = int.TryParse(index, out var i) ? i : 0;
        CurrentPage = AppPage.Reports;
    }

    // ----- Rename / exclude (features 1, 2, 6) -----

    public async Task RenameInstitutionAsync(InstitutionItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        await _institutions.RenameAsync(item.Id, newName);
        item.Name = newName.Trim();
        StatusMessage = $"Renamed institution to \"{item.Name}\".";
        await RefreshAsync();
    }

    /// <summary>Null or blank display name reverts to the bank-supplied name.</summary>
    public async Task RenameAccountAsync(AccountItem item, string? displayName)
    {
        var clean = string.IsNullOrWhiteSpace(displayName) || displayName.Trim() == item.BankName ? null : displayName.Trim();
        await _accounts.SetDisplayNameAsync(item.Id, clean);
        item.ApplyName(clean ?? item.BankName, clean);
        StatusMessage = clean is null ? $"\"{item.BankName}\" now uses the bank's name." : $"Renamed account to \"{clean}\" (bank name: {item.BankName}).";
        MarkDataChanged();
        await LoadTransactionsAsync();
    }

    private async Task OnAccountExcludeChangedAsync(AccountItem item, bool exclude)
    {
        await _accounts.SetExcludeFromNetWorthAsync(item.Id, exclude);
        StatusMessage = exclude ? $"\"{item.Name}\" is now excluded from net worth." : $"\"{item.Name}\" now counts toward net worth.";
        // Don't rebuild the Accounts collection while the grid is mid-edit; just recompute totals.
        await RecomputeTotalsAsync();
    }

    private async Task RecomputeTotalsAsync()
    {
        var summaries = await _accounts.ListSummariesAsync(includeHidden: false);
        var counted = summaries.Where(s => !s.ExcludeFromNetWorth).ToList();
        var liabilityCategoryIds = Categories.Where(c => c.Kind == AccountKind.Liability).Select(c => c.Id).ToHashSet();
        TotalAssets = counted.Where(s => s.CategoryId is null || !liabilityCategoryIds.Contains(s.CategoryId.Value)).Sum(s => s.Balance);
        TotalLiabilities = counted.Where(s => s.CategoryId is not null && liabilityCategoryIds.Contains(s.CategoryId.Value)).Sum(s => s.Balance);
        NetWorth = TotalAssets + TotalLiabilities;
        MarkDataChanged();
    }

    public void MarkDataChanged()
    {
        ReportsNeedRefresh = true;
        DashboardNeedsRefresh = true;
        BudgetNeedsRefresh = true;
        if (CurrentPage == AppPage.Budget) _ = OnPageActivatedAsync(AppPage.Budget);
        if (CurrentPage == AppPage.Dashboard) _ = OnPageActivatedAsync(AppPage.Dashboard);
        if (CurrentPage == AppPage.Reports) _ = OnPageActivatedAsync(AppPage.Reports);
    }
    public CategorizationService Categorization => _categorization;
    public ICategoryRepository CategoryRepository => _categoryRepo;
    public IAccountRepository AccountRepository => _accounts;
    public IHouseholdRepository HouseholdRepository => _household;

    /// <summary>Owner choices for the Accounts grid: "(no owner)" then household members in order.</summary>
    public ObservableCollection<HouseholdMember> Owners { get; } = new();

    /// <summary>Set by the window: shows the "create a rule?" dialog after a manual re-categorization.</summary>
    public Func<TransactionItem, CategoryOption, Task>? RuleSuggestionHandler { get; set; }

    public ObservableCollection<InstitutionItem> Institutions { get; } = new();
    public ObservableCollection<AccountItem> Accounts { get; } = new();
    public ObservableCollection<AccountCategory> Categories { get; } = new();
    public ObservableCollection<TransactionItem> Transactions { get; } = new();

    /// <summary>Pickable transaction categories: "(uncategorized)" then the flattened tree.</summary>
    public ObservableCollection<CategoryOption> TransactionCategories { get; } = new();
    /// <summary>Filter choices: All, Uncategorized only, then the tree (parents match their children too).</summary>
    public ObservableCollection<CategoryOption> FilterCategoryOptions { get; } = new() { CategoryOption.All, CategoryOption.UncategorizedFilter };

    [ObservableProperty] private InstitutionItem? _selectedInstitution;
    public bool HasSelectedInstitution => SelectedInstitution is not null;
    partial void OnSelectedInstitutionChanged(InstitutionItem? value) => OnPropertyChanged(nameof(HasSelectedInstitution));
    [ObservableProperty] private AccountItem? _selectedAccount;
    [ObservableProperty] private TransactionItem? _selectedTransaction;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private decimal _netWorth;
    [ObservableProperty] private decimal _totalAssets;
    [ObservableProperty] private decimal _totalLiabilities;

    // Transaction filter / paging
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private DateTime? _filterFrom;
    [ObservableProperty] private DateTime? _filterTo;
    [ObservableProperty] private CategoryOption? _filterCategory;
    /// <summary>Transactions filter: account type, then the accounts of that type.</summary>
    public ObservableCollection<TypeFilterOption> FilterTypeOptions { get; } = new() { TypeFilterOption.All };
    public ObservableCollection<AccountFilterOption> FilterAccountOptions { get; } = new() { AccountFilterOption.All };
    private readonly List<AccountFilterOption> _allAccountFilterOptions = new();
    [ObservableProperty] private TypeFilterOption _filterType = TypeFilterOption.All;
    [ObservableProperty] private AccountFilterOption _filterAccount = AccountFilterOption.All;
    private bool _rebuildingAccountFilter;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _totalCount;
    public int PageSize { get; } = 500;
    public string PageText => TotalCount == 0 ? "No transactions" : $"{PageIndex * PageSize + 1}-{Math.Min((PageIndex + 1) * PageSize, TotalCount)} of {TotalCount:N0}";
    public bool CanPrev => PageIndex > 0;
    public bool CanNext => (PageIndex + 1) * PageSize < TotalCount;

    partial void OnFilterCategoryChanged(CategoryOption? value) { PageIndex = 0; _ = LoadTransactionsAsync(); }

    partial void OnFilterTypeChanged(TypeFilterOption value)
    {
        if (value is null) return; // the ComboBox pushes null while its list is rebuilt
        RebuildAccountFilterOptions();
        PageIndex = 0;
        _ = LoadTransactionsAsync();
    }

    partial void OnFilterAccountChanged(AccountFilterOption value)
    {
        if (value is null || _rebuildingAccountFilter) return;
        PageIndex = 0;
        _ = LoadTransactionsAsync();
    }

    /// <summary>Narrows the account drop-down to the chosen type, keeping the selection when it still fits.</summary>
    private void RebuildAccountFilterOptions()
    {
        _rebuildingAccountFilter = true;
        try
        {
            var keepId = FilterAccount?.Id ?? 0;
            var type = FilterType ?? TypeFilterOption.All;
            FilterAccountOptions.Clear();
            FilterAccountOptions.Add(AccountFilterOption.All);
            foreach (var o in _allAccountFilterOptions)
            {
                var fits = type.Id == TypeFilterOption.AllId || (type.Id == TypeFilterOption.UntypedId ? o.TypeId is null : o.TypeId == type.Id);
                if (fits) FilterAccountOptions.Add(o);
            }
            _filterAccount = FilterAccountOptions.FirstOrDefault(o => o.Id == keepId) ?? AccountFilterOption.All;
            OnPropertyChanged(nameof(FilterAccount));
        }
        finally { _rebuildingAccountFilter = false; }
    }
    partial void OnPageIndexChanged(int value) { OnPropertyChanged(nameof(PageText)); OnPropertyChanged(nameof(CanPrev)); OnPropertyChanged(nameof(CanNext)); }
    partial void OnTotalCountChanged(int value) { OnPropertyChanged(nameof(PageText)); OnPropertyChanged(nameof(CanPrev)); OnPropertyChanged(nameof(CanNext)); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var selectedInstId = SelectedInstitution?.Id;
            var selectedAcctId = SelectedAccount?.Id;

            Categories.Clear();
            Categories.Add(new AccountCategory { Id = 0, Name = "(uncategorized)" });
            foreach (var c in await _accounts.ListCategoriesAsync()) Categories.Add(c);

            Owners.Clear();
            Owners.Add(new HouseholdMember { Id = 0, Name = "(no owner)" });
            foreach (var m in await _household.ListAsync()) Owners.Add(m);

            await ReloadTransactionCategoriesAsync();

            Institutions.Clear();
            foreach (var i in await _institutions.ListAsync()) Institutions.Add(InstitutionItem.From(i));
            SelectedInstitution = Institutions.FirstOrDefault(i => i.Id == selectedInstId);

            Accounts.Clear();
            var summaries = await _accounts.ListSummariesAsync(includeHidden: false);
            foreach (var s in summaries) Accounts.Add(new AccountItem(s, _accounts) { ExcludeChanged = OnAccountExcludeChangedAsync, OwnerChanged = _ => { MarkDataChanged(); return Task.CompletedTask; } });
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedAcctId);
            PossibleDuplicateCount = summaries.Count(s => s.PossibleDuplicateOf is not null);

            // Transactions filter choices (bank — account, like the dashboard), rebuilt without firing a reload.
            _rebuildingAccountFilter = true;
            try
            {
                var keepType = FilterType?.Id ?? TypeFilterOption.AllId;
                FilterTypeOptions.Clear();
                FilterTypeOptions.Add(TypeFilterOption.All);
                foreach (var c in Categories.Where(c => c.Id > 0)) FilterTypeOptions.Add(new TypeFilterOption(c.Id, c.Name));
                FilterTypeOptions.Add(TypeFilterOption.Untyped);
                _filterType = FilterTypeOptions.FirstOrDefault(t => t.Id == keepType) ?? TypeFilterOption.All;
                OnPropertyChanged(nameof(FilterType));
                _allAccountFilterOptions.Clear();
                foreach (var a in summaries.OrderBy(a => string.IsNullOrWhiteSpace(a.OrgName) ? a.InstitutionName : a.OrgName).ThenBy(a => a.Name))
                    _allAccountFilterOptions.Add(new AccountFilterOption(a.Id, $"{(string.IsNullOrWhiteSpace(a.OrgName) ? a.InstitutionName : a.OrgName)} — {a.Name}", a.CategoryId));
            }
            finally { _rebuildingAccountFilter = false; }
            RebuildAccountFilterOptions();

            var counted = summaries.Where(s => !s.ExcludeFromNetWorth).ToList();
            var liabilityCategoryIds = Categories.Where(c => c.Kind == AccountKind.Liability).Select(c => c.Id).ToHashSet();
            TotalAssets = counted.Where(s => s.CategoryId is null || !liabilityCategoryIds.Contains(s.CategoryId.Value)).Sum(s => s.Balance);
            TotalLiabilities = counted.Where(s => s.CategoryId is not null && liabilityCategoryIds.Contains(s.CategoryId.Value)).Sum(s => s.Balance);
            NetWorth = TotalAssets + TotalLiabilities; // liabilities are typically reported as negative balances
            MarkDataChanged();

            await LoadTransactionsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReloadTransactionCategoriesAsync()
    {
        var cats = await _categoryRepo.ListCategoriesAsync(includeHidden: false);
        var flat = CategoryOption.Flatten(cats);

        TransactionCategories.Clear();
        TransactionCategories.Add(CategoryOption.Uncategorized);
        foreach (var o in flat) TransactionCategories.Add(o);

        var keepId = FilterCategory?.Id ?? CategoryOption.AllId;
        FilterCategoryOptions.Clear();
        FilterCategoryOptions.Add(CategoryOption.All);
        FilterCategoryOptions.Add(CategoryOption.UncategorizedFilter);
        foreach (var o in flat) FilterCategoryOptions.Add(o);
        _filterCategory = FilterCategoryOptions.FirstOrDefault(o => o.Id == keepId) ?? FilterCategoryOptions[0];
        OnPropertyChanged(nameof(FilterCategory));
    }

    private TransactionFilter BuildFilter() => new()
    {
        AccountId = FilterAccount is { Id: > 0 } fa ? fa.Id : null,
        AccountTypeId = FilterType is { Id: > 0 } ft ? ft.Id : null,
        UntypedAccountsOnly = FilterType?.Id == TypeFilterOption.UntypedId,
        // posted_at_utc holds UTC midnight of the posting date, so compare against plain dates (no local-time shift).
        FromUtc = FilterFrom?.Date,
        ToUtc = FilterTo?.Date.AddDays(1),
        CategoryId = FilterCategory is { Id: > 0 } c ? c.Id : null,
        UncategorizedOnly = FilterCategory?.Id == CategoryOption.UncategorizedFilterId,
        Text = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText,
        Limit = PageSize,
        Offset = PageIndex * PageSize,
    };

    [RelayCommand]
    public async Task LoadTransactionsAsync()
    {
        var filter = BuildFilter();
        TotalCount = await _transactions.CountAsync(filter);
        if (PageIndex > 0 && PageIndex * PageSize >= TotalCount) { PageIndex = 0; filter.Offset = 0; }
        var rows = await _transactions.ListAsync(filter);
        Transactions.Clear();
        foreach (var r in rows) Transactions.Add(new TransactionItem(r, OnTransactionCategoryChangedAsync, OnTransactionNotesChangedAsync));
    }

    [RelayCommand]
    private Task ApplyFilterAsync() { PageIndex = 0; return LoadTransactionsAsync(); }

    [RelayCommand]
    private Task ClearFilterAsync()
    {
        FilterText = "";
        FilterFrom = null;
        FilterTo = null;
        FilterCategory = FilterCategoryOptions[0];
        _filterType = TypeFilterOption.All; OnPropertyChanged(nameof(FilterType));
        RebuildAccountFilterOptions();
        PageIndex = 0;
        return LoadTransactionsAsync();
    }

    [RelayCommand]
    private Task NextPageAsync() { if (CanNext) PageIndex++; return LoadTransactionsAsync(); }

    [RelayCommand]
    private Task PrevPageAsync() { if (CanPrev) PageIndex--; return LoadTransactionsAsync(); }

    private async Task OnTransactionCategoryChangedAsync(TransactionItem item, long? categoryId)
    {
        await _categorization.SetManualCategoryAsync(item.Id, categoryId);
        var option = TransactionCategories.FirstOrDefault(o => o.Id == (categoryId ?? 0));
        item.Apply(categoryId, categoryId is null ? CategorySource.None : CategorySource.Manual, categoryId is null ? "" : option?.Path ?? "");

        if (categoryId is not null && option is not null && RuleSuggestionHandler is not null)
            await RuleSuggestionHandler(item, option);
    }

    private Task OnTransactionNotesChangedAsync(TransactionItem item, string? notes) => _transactions.SetNotesAsync(item.Id, notes);

    [RelayCommand]
    private async Task RunRulesAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _categorization.RecategorizeAllAsync();
            StatusMessage = $"Rules applied: {result.Categorized} of {result.Examined} non-manual transactions categorized"
                     + (result.FromImported > 0 ? $" ({result.FromImported} replacing a category that came from an import)" : "")
                     + $", {result.Uncategorized} still uncategorized.";
            await LoadTransactionsAsync();
            MarkDataChanged();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SyncAllAsync()
    {
        IsBusy = true;
        StatusMessage = "Syncing all institutions...";
        var flagged = 0;
        try
        {
            var results = await _sync.SyncAllAsync();
            flagged = results.Sum(r => r.PossibleDuplicates);
            StatusMessage = Summarize(results);
        }
        catch (Exception ex)
        {
            StatusMessage = "Sync failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
        if (flagged > 0) DuplicatesDetected?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SyncSelectedAsync()
    {
        if (SelectedInstitution is null) return;
        IsBusy = true;
        StatusMessage = $"Syncing {SelectedInstitution.Name}...";
        var flagged = 0;
        try
        {
            var r = await _sync.SyncInstitutionAsync(SelectedInstitution.Id);
            flagged = r.PossibleDuplicates;
            StatusMessage = Summarize([r]);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
        if (flagged > 0) DuplicatesDetected?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedInstitution is null) return;
        var returned = await _institutionService.RemoveAsync(SelectedInstitution.Id);
        StatusMessage = returned > 0
            ? $"Removed {SelectedInstitution.Name}. {returned} account(s) it had taken over went back to their earlier connection with their history."
            : $"Removed {SelectedInstitution.Name} and all of its data.";
        await RefreshAsync();
    }

    private static string Summarize(IReadOnlyList<SyncResult> results)
    {
        var failed = results.Count(r => r.Status == SyncStatus.Failed);
        var added = results.Sum(r => r.TransactionsAdded);
        var updated = results.Sum(r => r.TransactionsUpdated);
        var accounts = results.Sum(r => r.AccountsSeen);
        var text = $"Synced {results.Count} institution(s), {accounts} account(s): {added} new transaction(s), {updated} updated.";
        var duplicates = results.Sum(r => r.PossibleDuplicates);
        if (duplicates > 0) text += $" {duplicates} new account(s) look like duplicates — see Accounts.";
        if (failed > 0) text += $" {failed} failed: " + string.Join("; ", results.Where(r => r.Error is not null).Select(r => r.Error));
        return text;
    }
}
