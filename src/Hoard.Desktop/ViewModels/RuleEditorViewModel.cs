using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Categorization;
using Hoard.Core.Data;
using Hoard.Core.Domain;

namespace Hoard.Desktop.ViewModels;

public sealed record AccountOption(long? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Backs <see cref="Views.RuleEditorWindow"/> in two modes: "suggest" (after the user re-categorizes a transaction)
/// and "edit" (from the rules manager). On save the rule is inserted/updated and applied to matching transactions.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly CategorizationService _categorization;
    private readonly ICategoryRepository _categories;
    private readonly CategoryRule _rule;

    public RuleEditorViewModel(
        CategorizationService categorization,
        ICategoryRepository categories,
        IEnumerable<CategoryOption> categoryOptions,
        IEnumerable<AccountOption> accountOptions,
        CategoryRule? existing,
        string? sampleDescription = null,
        string? samplePayee = null)
    {
        _categorization = categorization;
        _categories = categories;
        _rule = existing ?? new CategoryRule();
        IsNew = existing is null;

        foreach (var c in categoryOptions.Where(c => c.Id > 0)) CategoryOptions.Add(c);
        AccountOptions.Add(new AccountOption(null, "Any account"));
        foreach (var a in accountOptions) AccountOptions.Add(a);

        Pattern = _rule.Pattern;
        MatchField = _rule.MatchField;
        MatchType = _rule.MatchType;
        Priority = _rule.Priority;
        AmountMin = _rule.AmountMin;
        AmountMax = _rule.AmountMax;
        IsEnabled = _rule.IsEnabled;
        SelectedCategory = CategoryOptions.FirstOrDefault(c => c.Id == _rule.CategoryId);
        SelectedAccount = AccountOptions.FirstOrDefault(a => a.Id == _rule.AccountId) ?? AccountOptions[0];
        SampleText = sampleDescription ?? "";
        if (IsNew && string.IsNullOrWhiteSpace(Pattern))
            Pattern = MerchantKey.Extract(sampleDescription, samplePayee);
    }

    public bool IsNew { get; }
    public bool IsSystemRule => _rule.IsSystem;
    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();
    public ObservableCollection<AccountOption> AccountOptions { get; } = new();
    public IReadOnlyList<RuleMatchField> MatchFields { get; } = [RuleMatchField.Any, RuleMatchField.Description, RuleMatchField.Payee];
    public IReadOnlyList<RuleMatchType> MatchTypes { get; } = [RuleMatchType.Contains, RuleMatchType.StartsWith, RuleMatchType.Exact, RuleMatchType.Regex];

    [ObservableProperty] private string _pattern = "";
    [ObservableProperty] private RuleMatchField _matchField = RuleMatchField.Any;
    [ObservableProperty] private RuleMatchType _matchType = RuleMatchType.Contains;
    [ObservableProperty] private int _priority = 100;
    [ObservableProperty] private decimal? _amountMin;
    [ObservableProperty] private decimal? _amountMax;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private CategoryOption? _selectedCategory;
    [ObservableProperty] private AccountOption? _selectedAccount;
    [ObservableProperty] private string _sampleText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set after a successful save: how many existing transactions the rule was applied to.</summary>
    public int AppliedCount { get; private set; }
    public bool Saved { get; private set; }

    public string SampleMatchText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SampleText) || string.IsNullOrWhiteSpace(Pattern)) return "";
            // Account/amount scoping is not meaningful for the sample; check the text pattern only.
            var input = new CategorizationInput(0, SampleText, null, 0m);
            var textOnly = new CategoryRule { Id = 1, Pattern = Pattern, MatchField = RuleMatchField.Description, MatchType = MatchType, CategoryId = 1 };
            return new CategorizationEngine([textOnly]).Match(input) is not null
                ? "✔ Pattern matches this transaction."
                : "✘ Pattern does not match this transaction.";
        }
    }

    partial void OnPatternChanged(string value) => OnPropertyChanged(nameof(SampleMatchText));
    partial void OnMatchTypeChanged(RuleMatchType value) => OnPropertyChanged(nameof(SampleMatchText));

    private CategoryRule Snapshot(long id) => new()
    {
        Id = id,
        Pattern = Pattern.Trim(),
        MatchField = MatchField,
        MatchType = MatchType,
        Priority = Priority,
        AmountMin = AmountMin,
        AmountMax = AmountMax,
        AccountId = SelectedAccount?.Id,
        CategoryId = SelectedCategory?.Id ?? 0,
        IsEnabled = IsEnabled,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Pattern)) { Status = "Enter a pattern."; return; }
        if (SelectedCategory is null) { Status = "Choose a category."; return; }
        IsBusy = true;
        try
        {
            var draft = Snapshot(_rule.Id);
            if (IsNew)
            {
                var (_, applied) = await _categorization.CreateRuleAndApplyAsync(draft);
                AppliedCount = applied;
                Status = $"Rule created and applied to {applied} transaction(s).";
            }
            else
            {
                await _categories.UpdateRuleAsync(draft);
                var result = await _categorization.RecategorizeAllAsync();
                AppliedCount = result.Categorized;
                Status = "Rule saved; rules re-applied"
                       + (result.FromImported > 0 ? $" — {result.FromImported} imported category assignment(s) replaced." : ".");
            }
            Saved = true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
