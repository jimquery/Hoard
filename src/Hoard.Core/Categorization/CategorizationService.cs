using Hoard.Core.Data;
using Hoard.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Categorization;

/// <summary>What one categorization run did.</summary>
/// <param name="Examined">Transactions the rules were run against.</param>
/// <param name="Categorized">Of those, how many a rule claimed.</param>
/// <param name="Uncategorized">Of those, how many no rule claimed.</param>
/// <param name="FromImported">Of <paramref name="Categorized"/>, how many had carried a category from a CSV import that a rule has now replaced.</param>
public sealed record CategorizationResult(int Examined, int Categorized, int Uncategorized, int FromImported = 0);

/// <summary>
/// Applies rules to transactions. Manual assignments are never overwritten. Rule-assigned categories are
/// re-evaluated only when explicitly asked (e.g. after the user edits rules), so day-to-day syncs are cheap.
///
/// Categories that came from a CSV import are in scope whenever the user asks for rules explicitly — a manual
/// "apply rules" run, saving an edited rule, or creating a new one — because a rule is the user's own, current
/// intent and an imported category is a snapshot of what another app thought at export time. They are out of scope
/// for the automatic post-sync pass, which only fills in transactions that have no category at all. An imported row
/// a rule claims becomes <see cref="CategorySource.Rule"/> and follows the rules from then on; one that no rule
/// matches keeps its imported category untouched.
/// </summary>
public sealed class CategorizationService
{
    private readonly ICategoryRepository _categories;
    private readonly ITransactionRepository _transactions;
    private readonly ILogger<CategorizationService> _log;

    public CategorizationService(ICategoryRepository categories, ITransactionRepository transactions, ILogger<CategorizationService> log)
    {
        _categories = categories;
        _transactions = transactions;
        _log = log;
    }

    /// <summary>
    /// Categorize transactions that have no category yet (CategorySource.None). Used after every sync and at
    /// start-up: imported and rule-assigned categories are left alone, so this stays cheap.
    /// </summary>
    public Task<CategorizationResult> CategorizeNewAsync(long? institutionId = null, CancellationToken ct = default) =>
        RunAsync(includeRuleAssigned: false, includeImported: false, institutionId, ct);

    /// <summary>
    /// Re-run all rules over everything that is not manually categorized, imported categories included.
    /// Used when the user asks for rules explicitly (the "Apply rules" command, or after editing a rule).
    /// </summary>
    public Task<CategorizationResult> RecategorizeAllAsync(CancellationToken ct = default) =>
        RunAsync(includeRuleAssigned: true, includeImported: true, institutionId: null, ct);

    private async Task<CategorizationResult> RunAsync(bool includeRuleAssigned, bool includeImported, long? institutionId, CancellationToken ct)
    {
        var rules = await _categories.ListRulesAsync(ct);
        var engine = new CategorizationEngine(rules);
        var candidates = await _transactions.ListForCategorizationAsync(includeRuleAssigned, includeImported, institutionId, ct);

        var hits = new Dictionary<long, int>();
        int categorized = 0;
        int fromImported = 0;
        foreach (var t in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var rule = engine.Match(new CategorizationInput(t.AccountId, t.Description, t.Payee, t.Amount));
            if (rule is null)
            {
                // A row a rule used to own goes back to uncategorized; an imported category is left as the file had it.
                if (t.CategorySource == CategorySource.Rule)
                    await _transactions.SetCategoryAsync(t.Id, null, CategorySource.None, null, ct);
                continue;
            }
            if (t.CategoryId != rule.CategoryId || t.CategoryRuleId != rule.Id || t.CategorySource != CategorySource.Rule)
            {
                if (t.CategorySource == CategorySource.Imported) fromImported++;
                await _transactions.SetCategoryAsync(t.Id, rule.CategoryId, CategorySource.Rule, rule.Id, ct);
            }
            categorized++;
            hits[rule.Id] = hits.GetValueOrDefault(rule.Id) + 1;
        }

        foreach (var (ruleId, count) in hits)
            await _categories.RecordRuleHitsAsync(ruleId, count, ct);

        var result = new CategorizationResult(candidates.Count, categorized, candidates.Count - categorized, fromImported);
        _log.LogInformation("Categorization: {Examined} examined, {Categorized} matched by {Rules} rules ({FromImported} replaced an imported category)",
            result.Examined, result.Categorized, engine.RuleCount, result.FromImported);
        return result;
    }

    /// <summary>Manual override from the UI. Marks the transaction so rules never touch it again.</summary>
    public Task SetManualCategoryAsync(long transactionId, long? categoryId, CancellationToken ct = default) =>
        _transactions.SetCategoryAsync(transactionId, categoryId, categoryId is null ? CategorySource.None : CategorySource.Manual, null, ct);

    /// <summary>
    /// "Learn from edit": create a rule and immediately apply it to every non-manual transaction it matches,
    /// including rows whose category came from a CSV import. Returns the number of transactions it categorized.
    /// </summary>
    public async Task<(CategoryRule Rule, int Applied)> CreateRuleAndApplyAsync(CategoryRule rule, CancellationToken ct = default)
    {
        rule.Id = await _categories.InsertRuleAsync(rule, ct);
        var applied = await ApplySingleRuleAsync(rule, ct);
        return (rule, applied);
    }

    private async Task<int> ApplySingleRuleAsync(CategoryRule rule, CancellationToken ct)
    {
        var engine = new CategorizationEngine([rule]);
        var candidates = await _transactions.ListForCategorizationAsync(includeRuleAssigned: true, includeImported: true, institutionId: null, ct);
        int applied = 0;
        foreach (var t in candidates)
        {
            if (engine.Match(new CategorizationInput(t.AccountId, t.Description, t.Payee, t.Amount)) is null) continue;
            await _transactions.SetCategoryAsync(t.Id, rule.CategoryId, CategorySource.Rule, rule.Id, ct);
            applied++;
        }
        if (applied > 0) await _categories.RecordRuleHitsAsync(rule.Id, applied, ct);
        return applied;
    }
}
