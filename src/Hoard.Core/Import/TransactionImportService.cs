using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hoard.Core.Categorization;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Import;

/// <summary>Everything the importer needs to decide what to do with a parsed file.</summary>
public sealed class ImportPlan
{
    public string Source { get; init; } = EmpowerDefaults.SourceName;
    public IReadOnlyList<ImportedTransaction> Rows { get; init; } = [];
    /// <summary>Marker account id: create a history-only account under the "Imported" institution for this exported account.</summary>
    public const long CreateAccount = -1;
    /// <summary>Exported account name → Hoard account id; <see cref="CreateAccount"/> = create one; null (or missing) = skip those rows.</summary>
    public Dictionary<string, long?> Accounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Exported category name → Hoard "Group: Sub" path (created on import if missing); null = leave uncategorized.</summary>
    public Dictionary<string, CategoryTarget> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A stored transaction with the same amount within this many days counts as the same transaction.</summary>
    public int DedupeWindowDays { get; init; } = 3;
}

public sealed class ImportAccountOutcome
{
    public string SourceAccount { get; init; } = "";
    public long? AccountId { get; init; }
    public string AccountName { get; init; } = "";
    public int Rows { get; init; }
    public int Duplicates { get; set; }
    public int AlreadyImported { get; set; }
    public int New { get; set; }
    /// <summary>Rows whose category came from a Hoard rule rather than from the file.</summary>
    public int RuleCategorized { get; set; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public bool Skipped => AccountId is null;
    public bool WillCreate => AccountId == ImportPlan.CreateAccount;
}

public sealed class ImportOutcome
{
    public bool DryRun { get; init; }
    public int TotalRows { get; init; }
    public int SkippedRows { get; set; }
    public int Duplicates { get; set; }
    public int AlreadyImported { get; set; }
    public int Imported { get; set; }
    /// <summary>Rows whose category came from a Hoard rule rather than from the file.</summary>
    public int RuleCategorized { get; set; }
    public int CategoriesCreated { get; set; }
    public List<string> CategoriesToCreate { get; } = new();
    public int AccountsCreated { get; set; }
    public List<string> AccountsToCreate { get; } = new();
    public List<ImportAccountOutcome> Accounts { get; } = new();
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Imports exported transactions into mapped Hoard accounts without creating duplicates:
///  - a row already imported from the same file keys on its deterministic external id ("empower:…") and is only refreshed;
///  - a row that matches a stored transaction (same account and amount, posted within <see cref="ImportPlan.DedupeWindowDays"/>)
///    is treated as that transaction and skipped — bank feeds and exports rarely agree on the exact description, so the
///    description is deliberately not part of the match; each stored transaction can absorb only one exported row.
///
/// Categories: every row is run through the rule engine first, and a rule that matches wins over the category the
/// file supplied (such a row is stored as <see cref="CategorySource.Rule"/> and so follows later rule edits like any
/// other rule-categorized transaction). Only when no rule matches does the mapped file category apply.
/// </summary>
public sealed class TransactionImportService
{
    public const string ImportedInstitutionName = "Imported";

    private readonly IImportRepository _import;
    private readonly ICategoryRepository _categories;
    private readonly IAccountRepository _accounts;
    private readonly IInstitutionRepository _institutions;
    private readonly ILogger<TransactionImportService> _log;

    public TransactionImportService(IImportRepository import, ICategoryRepository categories, IAccountRepository accounts,
        IInstitutionRepository institutions, ILogger<TransactionImportService> log)
    {
        _import = import;
        _categories = categories;
        _accounts = accounts;
        _institutions = institutions;
        _log = log;
    }

    /// <summary>
    /// Creates a history-only account for an exported account that has no counterpart in Hoard. It lives under a disabled
    /// "Imported" institution (never synced), starts at a zero balance and is excluded from net worth, so its transactions
    /// feed the spending and income reports without distorting balances.
    /// </summary>
    private async Task<long> CreateImportedAccountAsync(string sourceAccount, string source, CancellationToken ct)
    {
        var inst = (await _institutions.ListAsync(ct)).FirstOrDefault(i => i.Provider == ProviderKind.Imported);
        if (inst is null)
        {
            inst = new Institution { Name = ImportedInstitutionName, Provider = ProviderKind.Imported, IsEnabled = false, CreatedAtUtc = DateTime.UtcNow };
            inst.Id = await _institutions.InsertAsync(inst, ct);
        }
        var externalId = ExternalIdPrefix(source) + "account:" + sourceAccount.Trim().ToLowerInvariant();
        var id = await _accounts.UpsertAsync(new Account
        {
            InstitutionId = inst.Id,
            ExternalId = externalId,
            Name = sourceAccount.Trim(),
            OrgName = source,
            Currency = "USD",
            Balance = 0m,
            BalanceDateUtc = DateTime.UtcNow,
        }, ct);
        await _accounts.SetExcludeFromNetWorthAsync(id, true, ct);
        return id;
    }

    /// <summary>
    /// Initial account and category mappings: what was saved last time, else the built-in suggestions. An exported account
    /// with no saved mapping and no convincing match comes back as <see cref="ImportPlan.CreateAccount"/>; null means
    /// "skip" was chosen for it before.
    /// </summary>
    public async Task<(Dictionary<string, long?> Accounts, Dictionary<string, CategoryTarget> Categories)> SuggestMappingsAsync(
        string source, IReadOnlyList<ImportedTransaction> rows, CancellationToken ct = default)
    {
        var saved = await _import.ListMappingsAsync(source, ct);
        var accounts = await _accounts.ListSummariesAsync(includeHidden: true, ct);
        var cats = await _categories.ListCategoriesAsync(includeHidden: true, ct);
        var pathById = cats.Where(c => c.ParentId is not null)
            .ToDictionary(c => c.Id, c => cats.First(p => p.Id == c.ParentId).Name + ": " + c.Name);
        foreach (var g in cats.Where(c => c.ParentId is null)) pathById[g.Id] = g.Name;

        var accountMap = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in rows.Select(r => r.Account).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stored = saved.FirstOrDefault(m => m.Kind == ImportMappingKind.Account && m.SourceKey.Equals(name, StringComparison.OrdinalIgnoreCase));
            accountMap[name] = stored is not null
                ? (stored.TargetId is { } id && accounts.Any(a => a.Id == id) ? id : null)
                : EmpowerDefaults.SuggestAccount(name, accounts)?.Id ?? ImportPlan.CreateAccount;
        }

        var categoryMap = new Dictionary<string, CategoryTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in rows.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stored = saved.FirstOrDefault(m => m.Kind == ImportMappingKind.Category && m.SourceKey.Equals(name, StringComparison.OrdinalIgnoreCase));
            categoryMap[name] = stored is not null
                ? new CategoryTarget(stored.TargetId is { } id && pathById.TryGetValue(id, out var path) ? path : null)
                : EmpowerDefaults.Suggest(name);
        }
        return (accountMap, categoryMap);
    }

    public Task<ImportOutcome> PreviewAsync(ImportPlan plan, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        RunAsync(plan, dryRun: true, progress, ct);

    public Task<ImportOutcome> ImportAsync(ImportPlan plan, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        RunAsync(plan, dryRun: false, progress, ct);

    private async Task<ImportOutcome> RunAsync(ImportPlan plan, bool dryRun, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var accounts = (await _accounts.ListSummariesAsync(includeHidden: true, ct)).ToDictionary(a => a.Id);
        var categoryIds = new CategoryResolver(_categories, dryRun);
        await categoryIds.LoadAsync(ct);
        var engine = new CategorizationEngine(await _categories.ListRulesAsync(ct));
        var ruleHits = new Dictionary<long, int>();

        var groups = plan.Rows.GroupBy(r => r.Account, StringComparer.OrdinalIgnoreCase).ToList();
        var outcome = new ImportOutcome { DryRun = dryRun, TotalRows = plan.Rows.Count };
        var done = 0;
        var prefix = ExternalIdPrefix(plan.Source);

        // Builds one row and tallies the rule that claimed it, if any. Hits are only written on a real import.
        async Task<Transaction> Build(ImportedTransaction row, long account, string externalId, ImportAccountOutcome? into)
        {
            var tx = await BuildAsync(row, account, externalId, plan, categoryIds, engine, ct);
            if (tx.CategorySource == CategorySource.Rule && tx.CategoryRuleId is { } hit)
            {
                ruleHits[hit] = ruleHits.GetValueOrDefault(hit) + 1;
                outcome.RuleCategorized++;
                if (into is not null) into.RuleCategorized++;
            }
            return tx;
        }

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var rows = g.OrderBy(r => r.Date).ThenBy(r => r.Line).ToList();
            plan.Accounts.TryGetValue(g.Key, out var accountId);
            if (accountId == ImportPlan.CreateAccount)
            {
                if (dryRun)
                {
                    outcome.AccountsToCreate.Add(g.Key);
                    var s = new ImportAccountOutcome { SourceAccount = g.Key, AccountId = ImportPlan.CreateAccount, AccountName = "(new account)", Rows = rows.Count, New = rows.Count, From = rows[0].Date, To = rows[^1].Date };
                    outcome.Accounts.Add(s);
                    outcome.Imported += rows.Count;
                    // Records the categories that would be created and which rows the rules would claim.
                    // The account does not exist yet, so account-scoped rules cannot match in this preview.
                    foreach (var r in rows) await Build(r, 0, "", s);
                    done += rows.Count;
                    progress?.Report((done, plan.Rows.Count));
                    continue;
                }
                accountId = await CreateImportedAccountAsync(g.Key, plan.Source, ct);
                plan.Accounts[g.Key] = accountId; // so the saved mapping points at the real account
                outcome.AccountsCreated++;
                accounts = (await _accounts.ListSummariesAsync(includeHidden: true, ct)).ToDictionary(a => a.Id);
            }
            var stats = new ImportAccountOutcome
            {
                SourceAccount = g.Key, AccountId = accountId, Rows = rows.Count,
                AccountName = accountId is { } aid && accounts.TryGetValue(aid, out var acct) ? acct.Name : "(skipped)",
                From = rows[0].Date, To = rows[^1].Date,
            };
            outcome.Accounts.Add(stats);
            if (accountId is null || !accounts.ContainsKey(accountId.Value))
            {
                outcome.SkippedRows += rows.Count;
                done += rows.Count;
                progress?.Report((done, plan.Rows.Count));
                continue;
            }

            var window = TimeSpan.FromDays(plan.DedupeWindowDays);
            var existing = await _import.ListForDedupeAsync(accountId.Value,
                Utc(rows[0].Date) - window, Utc(rows[^1].Date) + window + TimeSpan.FromDays(1), ct);
            var byExternalId = existing.Where(e => e.ExternalId is not null).Select(e => e.ExternalId!).ToHashSet(StringComparer.Ordinal);
            // Only transactions that did not come from this source can be "the same transaction seen through the bank".
            var candidates = existing.Where(e => e.ExternalId is null || !e.ExternalId.StartsWith(prefix, StringComparison.Ordinal))
                .GroupBy(e => e.Amount).ToDictionary(x => x.Key, x => x.Select(e => new Candidate(e.PostedAtUtc.Date)).ToList());
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();
                var key = RowKey(r);
                var n = occurrences.GetValueOrDefault(key);
                occurrences[key] = n + 1;
                var externalId = prefix + Hash(key + "|" + n.ToString(CultureInfo.InvariantCulture));

                if (byExternalId.Contains(externalId))
                {
                    stats.AlreadyImported++;
                    outcome.AlreadyImported++;
                    var refreshed = await Build(r, accountId.Value, externalId, stats);
                    if (!dryRun) await _import.InsertImportedAsync(refreshed, ct);
                }
                else if (candidates.TryGetValue(r.Amount, out var list) && TakeNearest(list, r.Date, plan.DedupeWindowDays))
                {
                    stats.Duplicates++;
                    outcome.Duplicates++;
                }
                else
                {
                    stats.New++;
                    outcome.Imported++;
                    var tx = await Build(r, accountId.Value, externalId, stats);
                    if (!dryRun) await _import.InsertImportedAsync(tx, ct);
                }
                done++;
                if (done % 50 == 0) progress?.Report((done, plan.Rows.Count));
            }
        }

        outcome.CategoriesToCreate.AddRange(categoryIds.Missing);
        outcome.CategoriesCreated = categoryIds.Created;
        if (!dryRun)
        {
            foreach (var (ruleId, count) in ruleHits)
                await _categories.RecordRuleHitsAsync(ruleId, count, ct);

            var mappings = plan.Accounts.Select(kv => new ImportMapping { Source = plan.Source, Kind = ImportMappingKind.Account, SourceKey = kv.Key, TargetId = kv.Value })
                .Concat(plan.Categories.Select(kv => new ImportMapping { Source = plan.Source, Kind = ImportMappingKind.Category, SourceKey = kv.Key, TargetId = categoryIds.IdForPath(kv.Value.Path) }));
            await _import.SaveMappingsAsync(mappings, ct);
            _log.LogInformation("Imported {Imported} transactions from {Source} ({Duplicates} duplicates, {Already} already imported, {Skipped} skipped, {RuleCategorized} categorized by rules)",
                outcome.Imported, plan.Source, outcome.Duplicates, outcome.AlreadyImported, outcome.SkippedRows, outcome.RuleCategorized);
        }
        progress?.Report((plan.Rows.Count, plan.Rows.Count));
        return outcome;
    }

    private sealed class Candidate(DateTime date) { public DateTime Date { get; } = date; public bool Taken { get; set; } }

    private static bool TakeNearest(List<Candidate> list, DateOnly date, int windowDays)
    {
        var target = Utc(date);
        Candidate? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in list)
        {
            if (c.Taken) continue;
            var distance = Math.Abs((c.Date - target).TotalDays);
            if (distance <= windowDays && distance < bestDistance) { best = c; bestDistance = distance; }
        }
        if (best is null) return false;
        best.Taken = true;
        return true;
    }

    private static async Task<Transaction> BuildAsync(ImportedTransaction r, long accountId, string externalId, ImportPlan plan,
        CategoryResolver categories, CategorizationEngine engine, CancellationToken ct)
    {
        var posted = Utc(r.Date);
        long? categoryId = null;
        long? ruleId = null;
        var source = CategorySource.None;

        // Rules first: they are the user's own, current intent, where an exported category is a snapshot of what
        // another app thought at export time. A rule-categorized row is stored as CategorySource.Rule, so later
        // rule edits re-evaluate it like any other; rows no rule claims keep the file's category and stay protected.
        if (engine.Match(new CategorizationInput(accountId, r.Description, r.Description, r.Amount)) is { } rule)
        {
            categoryId = rule.CategoryId;
            ruleId = rule.Id;
            source = CategorySource.Rule;
        }
        else if (plan.Categories.TryGetValue(r.Category, out var target) && target.For(r.Amount) is { } path)
        {
            categoryId = await categories.ResolveAsync(path, ct);
            if (categoryId is not null) source = CategorySource.Imported;
        }

        return new Transaction
        {
            AccountId = accountId,
            ExternalId = externalId,
            PostedAtUtc = posted,
            TransactedAtUtc = null,
            Amount = r.Amount,
            Description = r.Description,
            Payee = r.Description,
            Memo = r.Category.Length > 0 ? $"{plan.Source} category: {r.Category}" : null,
            Notes = r.Tags,
            IsPending = false,
            Fingerprint = TransactionFingerprint.Compute(accountId, posted, r.Amount, r.Description),
            CategoryId = categoryId,
            CategorySource = source,
            CategoryRuleId = ruleId,
        };
    }

    public static string ExternalIdPrefix(string source) => source.ToLowerInvariant() + ":";

    private static string RowKey(ImportedTransaction r) => string.Join("|",
        r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Account.Trim(), r.Description.Trim(),
        r.Amount.ToString("0.00##", CultureInfo.InvariantCulture), r.Category.Trim());

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();

    private static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Turns "Group: Sub" paths into category ids, creating groups/sub-categories as needed (or just listing them on a dry run).</summary>
    private sealed class CategoryResolver(ICategoryRepository repo, bool dryRun)
    {
        private readonly Dictionary<string, TransactionCategory> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> Missing => _missing;
        public int Created { get; private set; }

        public async Task LoadAsync(CancellationToken ct)
        {
            var all = await repo.ListCategoriesAsync(includeHidden: true, ct);
            foreach (var g in all.Where(c => c.ParentId is null)) _byPath[g.Name] = g;
            foreach (var c in all.Where(c => c.ParentId is not null))
            {
                var parent = all.FirstOrDefault(p => p.Id == c.ParentId);
                if (parent is not null) _byPath[parent.Name + ": " + c.Name] = c;
            }
        }

        public long? IdForPath(string? path) => path is not null && _byPath.TryGetValue(path, out var c) ? c.Id : null;

        public async Task<long?> ResolveAsync(string path, CancellationToken ct)
        {
            if (_byPath.TryGetValue(path, out var found)) return found.Id;
            var parts = path.Split(':', 2, StringSplitOptions.TrimEntries);
            var groupName = parts[0];
            var subName = parts.Length > 1 ? parts[1] : null;
            if (dryRun)
            {
                _missing.Add(path);
                return null;
            }
            if (!_byPath.TryGetValue(groupName, out var group))
            {
                group = new TransactionCategory { Name = groupName, Kind = KindFor(groupName), SortOrder = 900 };
                group.Id = await repo.InsertCategoryAsync(group, ct);
                _byPath[groupName] = group;
                Created++;
            }
            if (subName is null) return group.Id;
            var sub = new TransactionCategory { ParentId = group.Id, Name = subName, Kind = group.Kind, SortOrder = 900 };
            sub.Id = await repo.InsertCategoryAsync(sub, ct);
            _byPath[path] = sub;
            Created++;
            return sub.Id;
        }

        private static CategoryKind KindFor(string groupName) => groupName.ToLowerInvariant() switch
        {
            "income" => CategoryKind.Income,
            "transfer" or "transfers" => CategoryKind.Transfer,
            _ => CategoryKind.Expense,
        };
    }
}
