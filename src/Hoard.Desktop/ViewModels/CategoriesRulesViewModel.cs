using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Categorization;
using Hoard.Core.Data;
using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

/// <summary>Editable row for the categories grid; renaming persists immediately.</summary>
public sealed partial class CategoryItem : ObservableObject
{
    private readonly TransactionCategory _model;
    private readonly ICategoryRepository _repo;

    public CategoryItem(TransactionCategory model, string parentName, ICategoryRepository repo)
    {
        _model = model;
        _repo = repo;
        ParentName = parentName;
        _name = model.Name;
        _isHidden = model.IsHidden;
    }

    public long Id => _model.Id;
    public long? ParentId => _model.ParentId;
    public string ParentName { get; }
    public CategoryKind Kind => _model.Kind;
    public bool IsSystem => _model.IsSystem;
    public string Path => ParentId is null ? Name : $"{ParentName}: {Name}";

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isHidden;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == _model.Name) return;
        _model.Name = value.Trim();
        _ = _repo.UpdateCategoryAsync(_model);
        OnPropertyChanged(nameof(Path));
    }

    partial void OnIsHiddenChanged(bool value)
    {
        if (value == _model.IsHidden) return;
        _model.IsHidden = value;
        _ = _repo.UpdateCategoryAsync(_model);
    }
}

public sealed partial class CategoriesRulesViewModel : ObservableObject
{
    private readonly ICategoryRepository _repo;
    private readonly CategorizationService _categorization;
    private readonly IAccountRepository _accounts;

    public CategoriesRulesViewModel(ICategoryRepository repo, CategorizationService categorization, IAccountRepository accounts)
    {
        _repo = repo;
        _categorization = categorization;
        _accounts = accounts;
    }

    public CategorizationService Categorization => _categorization;
    public ICategoryRepository Repository => _repo;

    public ObservableCollection<CategoryRuleRow> Rules { get; } = new();
    public ObservableCollection<CategoryItem> Categories { get; } = new();
    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();
    public ObservableCollection<CategoryOption> ParentOptions { get; } = new();
    public ObservableCollection<AccountOption> AccountOptions { get; } = new();
    public IReadOnlyList<CategoryKind> Kinds { get; } = [CategoryKind.Expense, CategoryKind.Income, CategoryKind.Transfer];

    [ObservableProperty] private CategoryRuleRow? _selectedRule;
    [ObservableProperty] private CategoryItem? _selectedCategory;
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private CategoryOption? _newCategoryParent;
    [ObservableProperty] private CategoryKind _newCategoryKind = CategoryKind.Expense;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _changed;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var cats = await _repo.ListCategoriesAsync(includeHidden: true);
            var byId = cats.ToDictionary(c => c.Id);

            Categories.Clear();
            foreach (var opt in CategoryOption.Flatten(cats))
                Categories.Add(new CategoryItem(byId[opt.Id], opt.ParentId is { } p ? byId[p].Name : "", _repo));

            CategoryOptions.Clear();
            foreach (var o in CategoryOption.Flatten(cats.Where(c => !c.IsHidden))) CategoryOptions.Add(o);

            ParentOptions.Clear();
            ParentOptions.Add(new CategoryOption { Id = 0, Path = "(top-level group)" });
            foreach (var o in CategoryOptions.Where(o => o.IsParent)) ParentOptions.Add(o);
            NewCategoryParent ??= ParentOptions[0];

            AccountOptions.Clear();
            foreach (var a in await _accounts.ListSummariesAsync(includeHidden: true))
                AccountOptions.Add(new AccountOption(a.Id, $"{a.InstitutionName} - {a.Name}"));

            await ReloadRulesAsync();
        }
        finally { IsBusy = false; }
    }

    public async Task ReloadRulesAsync()
    {
        var selectedId = SelectedRule?.Id;
        Rules.Clear();
        foreach (var r in await _repo.ListRuleRowsAsync()) Rules.Add(r);
        SelectedRule = Rules.FirstOrDefault(r => r.Id == selectedId);
    }

    public CategoryRule ToRule(CategoryRuleRow r) => new()
    {
        Id = r.Id, Priority = r.Priority, MatchField = r.MatchField, MatchType = r.MatchType, Pattern = r.Pattern,
        AmountMin = r.AmountMin, AmountMax = r.AmountMax, AccountId = r.AccountId, CategoryId = r.CategoryId,
        IsEnabled = r.IsEnabled, IsSystem = r.IsSystem, HitCount = r.HitCount, LastHitAtUtc = r.LastHitAtUtc,
    };

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null) return;
        await _repo.DeleteRuleAsync(SelectedRule.Id);
        Changed = true;
        Status = $"Deleted rule \"{SelectedRule.Pattern}\". Transactions it categorized keep their category until you run the rules again.";
        await ReloadRulesAsync();
    }

    [RelayCommand]
    private async Task ToggleRuleAsync()
    {
        if (SelectedRule is null) return;
        var rule = ToRule(SelectedRule);
        rule.IsEnabled = !rule.IsEnabled;
        await _repo.UpdateRuleAsync(rule);
        Changed = true;
        await ReloadRulesAsync();
    }

    [RelayCommand]
    private async Task RunRulesAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _categorization.RecategorizeAllAsync();
            Changed = true;
            Status = $"Rules applied: {result.Categorized} of {result.Examined} non-manual transactions categorized"
                     + (result.FromImported > 0 ? $" ({result.FromImported} replacing a category that came from an import)" : "")
                     + $", {result.Uncategorized} still uncategorized.";
            await ReloadRulesAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = NewCategoryName.Trim();
        if (name.Length == 0) { Status = "Enter a category name."; return; }
        var parent = NewCategoryParent is { Id: > 0 } p ? p : null;
        var kind = parent?.Kind ?? NewCategoryKind;
        try
        {
            await _repo.InsertCategoryAsync(new TransactionCategory
            {
                ParentId = parent?.Id,
                Name = name,
                Kind = kind,
                SortOrder = 1000,
            });
            NewCategoryName = "";
            Changed = true;
            Status = $"Added \"{(parent is null ? name : parent.Name + ": " + name)}\".";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = ex.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)
                ? "A category with that name already exists under the same parent."
                : ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory is null) return;
        await _repo.DeleteCategoryAsync(SelectedCategory.Id);
        Changed = true;
        Status = $"Deleted \"{SelectedCategory.Path}\". Its transactions are now uncategorized and its rules were removed.";
        await LoadAsync();
    }
}
