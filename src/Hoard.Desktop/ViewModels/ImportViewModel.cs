using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Import;

namespace Hoard.Desktop.ViewModels;

/// <summary>A Hoard account offered in the mapping drop-down; Id 0 = skip, -1 = create a history-only account.</summary>
public sealed record AccountChoice(long Id, string Label)
{
    public static readonly AccountChoice Skip = new(0, "(skip — do not import)");
    public static readonly AccountChoice Create = new(ImportPlan.CreateAccount, "(create a new account under \"Imported\")");
    public override string ToString() => Label;
}

public sealed partial class AccountMapItem : ObservableObject
{
    public string Source { get; init; } = "";
    public int Rows { get; init; }
    public string Range { get; init; } = "";
    [ObservableProperty] private AccountChoice _target = AccountChoice.Skip;
    [ObservableProperty] private string _result = "";
    public bool IsSuggested { get; init; }
}

public sealed partial class CategoryMapItem : ObservableObject
{
    public string Source { get; init; } = "";
    public int Rows { get; init; }
    /// <summary>"Group: Sub" path, blank = leave uncategorized. Typing a new path creates that category on import.</summary>
    [ObservableProperty] private string _targetPath = "";
    /// <summary>Money-out rows can go elsewhere (e.g. Empower "Interest" is earned or charged); blank = same as TargetPath.</summary>
    [ObservableProperty] private string _negativePath = "";
    public bool HasSplit => NegativePath.Length > 0;
}

public sealed partial class ImportViewModel : ObservableObject
{
    private readonly TransactionImportService _service;
    private readonly IAccountRepository _accounts;
    private readonly ICategoryRepository _categories;
    private IReadOnlyList<ImportedTransaction> _rows = [];

    public ImportViewModel(TransactionImportService service, IAccountRepository accounts, ICategoryRepository categories)
    {
        _service = service;
        _accounts = accounts;
        _categories = categories;
    }

    public const string Source = EmpowerDefaults.SourceName;

    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileSummary = "";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private double _dedupeWindowDays = 3; // double: bound to a NumberBox
    public bool Changed { get; private set; }

    public ObservableCollection<AccountChoice> AccountChoices { get; } = new();
    public ObservableCollection<string> CategoryPaths { get; } = new();
    public ObservableCollection<AccountMapItem> AccountMaps { get; } = new();
    public ObservableCollection<CategoryMapItem> CategoryMaps { get; } = new();
    public ObservableCollection<string> Problems { get; } = new();

    public async Task LoadFileAsync(string path)
    {
        IsBusy = true;
        Status = "";
        try
        {
            FilePath = path;
            ImportParseResult parsed;
            using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                parsed = await Task.Run(() => CsvTransactionParser.Parse(reader));
            _rows = parsed.Rows;
            Problems.Clear();
            foreach (var p in parsed.Problems.Take(50)) Problems.Add(p);
            if (_rows.Count == 0)
            {
                IsLoaded = false;
                FileSummary = parsed.Problems.FirstOrDefault() ?? "No transactions found in the file.";
                return;
            }

            var accounts = await _accounts.ListSummariesAsync(includeHidden: true);
            AccountChoices.Clear();
            AccountChoices.Add(AccountChoice.Create);
            AccountChoices.Add(AccountChoice.Skip);
            // Prefix with the bank (as the provider reports it, like the dashboard), not the institution the connection was named after.
            static string Bank(AccountSummary a) => string.IsNullOrWhiteSpace(a.OrgName) ? a.InstitutionName : a.OrgName;
            foreach (var a in accounts.OrderBy(Bank).ThenBy(a => a.Name))
                AccountChoices.Add(new AccountChoice(a.Id, $"{Bank(a)} — {a.Name}" + (a.IsHidden ? " (hidden)" : "")));

            var cats = await _categories.ListCategoriesAsync(includeHidden: true);
            CategoryPaths.Clear();
            CategoryPaths.Add("");
            foreach (var g in cats.Where(c => c.ParentId is null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
            {
                CategoryPaths.Add(g.Name);
                foreach (var c in cats.Where(c => c.ParentId == g.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
                    CategoryPaths.Add($"{g.Name}: {c.Name}");
            }

            var (accountMap, categoryMap) = await _service.SuggestMappingsAsync(Source, _rows);
            AccountMaps.Clear();
            foreach (var g in _rows.GroupBy(r => r.Account, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
            {
                var suggestedId = accountMap.GetValueOrDefault(g.Key);
                AccountMaps.Add(new AccountMapItem
                {
                    Source = g.Key, Rows = g.Count(),
                    Range = $"{g.Min(r => r.Date):MMM yyyy} – {g.Max(r => r.Date):MMM yyyy}",
                    // No match in Hoard → create a history-only account (unless "skip" was saved for it last time).
                    Target = suggestedId is null ? AccountChoice.Skip : AccountChoices.FirstOrDefault(c => c.Id == suggestedId) ?? AccountChoice.Create,
                    IsSuggested = suggestedId is not null,
                });
            }
            CategoryMaps.Clear();
            foreach (var g in _rows.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
            {
                var t = categoryMap.GetValueOrDefault(g.Key) ?? new CategoryTarget(null);
                CategoryMaps.Add(new CategoryMapItem { Source = g.Key, Rows = g.Count(), TargetPath = t.Path ?? "", NegativePath = t.NegativePath ?? "" });
            }

            var mapped = AccountMaps.Count(a => a.Target.Id > 0);
            var toCreate = AccountMaps.Count(a => a.Target.Id == ImportPlan.CreateAccount);
            FileSummary = $"{_rows.Count:N0} transactions · {AccountMaps.Count} accounts ({mapped} matched to Hoard accounts, {toCreate} to be created) · {CategoryMaps.Count} categories · " +
                          $"{_rows.Min(r => r.Date):MMM d, yyyy} – {_rows.Max(r => r.Date):MMM d, yyyy}" +
                          (parsed.Problems.Count > 0 ? $" · {parsed.Problems.Count} unreadable line(s) skipped" : "");
            IsLoaded = true;
            HasPreview = false;
            PreviewSummary = "";
        }
        catch (Exception ex) { Status = ex.Message; IsLoaded = false; }
        finally { IsBusy = false; }
    }

    private ImportPlan BuildPlan() => new()
    {
        Source = Source,
        Rows = _rows,
        DedupeWindowDays = Math.Clamp((int)Math.Round(DedupeWindowDays), 0, 14),
        Accounts = AccountMaps.ToDictionary(a => a.Source, a => a.Target.Id == 0 ? (long?)null : a.Target.Id, StringComparer.OrdinalIgnoreCase),
        // (CreateAccount = -1 passes through and the service creates the account on import)
        Categories = CategoryMaps.ToDictionary(c => c.Source,
            c => new CategoryTarget(Clean(c.TargetPath), Clean(c.NegativePath)), StringComparer.OrdinalIgnoreCase),
    };

    private static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private Task PreviewAsync() => RunAsync(dryRun: true);

    [RelayCommand]
    private Task ImportAsync() => RunAsync(dryRun: false);

    private async Task RunAsync(bool dryRun)
    {
        if (!IsLoaded) return;
        IsBusy = true;
        Status = dryRun ? "Checking for duplicates…" : "Importing…";
        Progress = 0;
        try
        {
            var plan = BuildPlan();
            var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total);
            var outcome = dryRun ? await _service.PreviewAsync(plan, progress) : await _service.ImportAsync(plan, progress);

            foreach (var a in AccountMaps)
            {
                var o = outcome.Accounts.FirstOrDefault(x => x.SourceAccount.Equals(a.Source, StringComparison.OrdinalIgnoreCase));
                a.Result = o is null ? "" : o.Skipped ? "skipped" : o.WillCreate ? $"new account · {o.New:N0} transactions"
                    : $"{o.New:N0} new · {o.Duplicates:N0} already in Hoard" + (o.AlreadyImported > 0 ? $" · {o.AlreadyImported:N0} imported before" : "")
                      + (o.RuleCategorized > 0 ? $" · {o.RuleCategorized:N0} categorized by rules" : "");
            }

            var verb = dryRun ? "will be imported" : "imported";
            var ruled = dryRun ? "would take their category from your rules instead of the file" : "took their category from your rules instead of the file";
            PreviewSummary = $"{outcome.Imported:N0} transactions {verb} · {outcome.Duplicates:N0} match transactions already in Hoard and will be left alone · " +
                             $"{outcome.AlreadyImported:N0} imported previously · {outcome.SkippedRows:N0} in skipped accounts" +
                             (outcome.RuleCategorized > 0 ? $"\n{outcome.RuleCategorized:N0} row(s) {ruled}." : "") +
                             (dryRun && outcome.AccountsToCreate.Count > 0 ? $"\nNew history-only accounts to be created under \"Imported\" (excluded from net worth): {string.Join(", ", outcome.AccountsToCreate)}" : "") +
                             (!dryRun && outcome.AccountsCreated > 0 ? $"\n{outcome.AccountsCreated} account(s) created under \"Imported\"." : "") +
                             (dryRun && outcome.CategoriesToCreate.Count > 0 ? $"\nNew categories to be created: {string.Join(", ", outcome.CategoriesToCreate)}" : "") +
                             (!dryRun && outcome.CategoriesCreated > 0 ? $"\n{outcome.CategoriesCreated} categor{(outcome.CategoriesCreated == 1 ? "y" : "ies")} created." : "");
            HasPreview = true;
            if (!dryRun)
            {
                Changed = true;
                Status = $"Done — {outcome.Imported:N0} transactions imported.";
            }
            else Status = "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; Progress = 100; }
    }
}
