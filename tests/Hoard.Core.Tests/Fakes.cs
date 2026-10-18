using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Security;
using Hoard.Core.Sync;

namespace Hoard.Core.Tests;

/// <summary>In-memory stand-ins for the MySQL repositories so SyncService can be tested without a database.</summary>
public sealed class FakeDataKeyStore : IDataKeyStore
{
    public List<DataKeyRow> Rows { get; } = new();
    public Task<DataKeyRow?> GetLatestAsync(CancellationToken ct = default) => Task.FromResult(Rows.OrderByDescending(r => r.KeyVersion).FirstOrDefault());
    public Task<DataKeyRow?> GetAsync(int version, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(r => r.KeyVersion == version));
    public Task<int> InsertAsync(byte[] wrappedKey, string scope, CancellationToken ct = default)
    {
        var v = Rows.Count + 1;
        Rows.Add(new DataKeyRow(v, wrappedKey, scope, DateTime.UtcNow));
        return Task.FromResult(v);
    }
}

public sealed class FakeInstitutionRepository : IInstitutionRepository
{
    public List<Institution> Items { get; } = new();
    public Dictionary<long, SealedSecret> Credentials { get; } = new();
    public Task<IReadOnlyList<Institution>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Institution>>(Items.ToList());
    public Task<Institution?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
    public Task<long> InsertAsync(Institution i, CancellationToken ct = default) { i.Id = Items.Count + 1; Items.Add(i); return Task.FromResult(i.Id); }
    public Task UpdateSyncStateAsync(long id, SyncStatus status, string? error, DateTime? syncedAtUtc, CancellationToken ct = default)
    {
        var i = Items.First(x => x.Id == id);
        i.LastSyncStatus = status; i.LastError = error; if (syncedAtUtc is not null) i.LastSyncAtUtc = syncedAtUtc;
        return Task.CompletedTask;
    }
    public Task RenameAsync(long id, string name, CancellationToken ct = default) { Items.First(i => i.Id == id).Name = name; return Task.CompletedTask; }
    public Task DeleteAsync(long id, CancellationToken ct = default) { Items.RemoveAll(i => i.Id == id); return Task.CompletedTask; }
    public Task SaveCredentialAsync(long institutionId, SealedSecret s, CancellationToken ct = default) { Credentials[institutionId] = s; return Task.CompletedTask; }
    public Task<SealedSecret?> GetCredentialAsync(long institutionId, CancellationToken ct = default) => Task.FromResult(Credentials.GetValueOrDefault(institutionId));
}

public sealed class FakeAccountRepository : IAccountRepository
{
    public List<Account> Items { get; } = new();
    public List<BalanceSnapshot> Snapshots { get; } = new();
    public Task<IReadOnlyList<AccountSummary>> ListSummariesAsync(bool includeHidden = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AccountSummary>>(Items.Where(a => includeHidden || !a.IsHidden).Select(a => new AccountSummary
        {
            Id = a.Id, InstitutionId = a.InstitutionId, InstitutionName = "Institution " + a.InstitutionId,
            Name = string.IsNullOrEmpty(a.DisplayName) ? a.Name : a.DisplayName!, BankName = a.Name, DisplayName = a.DisplayName, OrgName = a.OrgName, ExternalId = a.ExternalId,
            CategoryId = a.CategoryId, OwnerId = a.OwnerId, Balance = a.Balance, ExcludeFromNetWorth = a.ExcludeFromNetWorth,
        }).ToList());
    public Task<IReadOnlyList<Account>> ListByInstitutionAsync(long institutionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Account>>(Items.Where(a => a.InstitutionId == institutionId).ToList());
    public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Account>>(Items.ToList());
    public Task SetPossibleDuplicateAsync(long accountId, long? ofAccountId, bool dismissed, CancellationToken ct = default)
    {
        var a = Items.First(x => x.Id == accountId);
        a.PossibleDuplicateOf = ofAccountId; a.DuplicateDismissed = dismissed;
        return Task.CompletedTask;
    }
    public List<(long Keep, long Absorb)> Merges { get; } = new();
    /// <summary>In-memory stand-in for the SQL merge: records the call, hands over the live identity, drops the absorbed row.</summary>
    public Task<AccountMergeResult> MergeAsync(long keepId, long absorbId, int dedupeWindowDays, CancellationToken ct = default)
    {
        var keep = Items.First(x => x.Id == keepId);
        var absorb = Items.First(x => x.Id == absorbId);
        var takeIdentity = (absorb.BalanceDateUtc ?? DateTime.MinValue) > (keep.BalanceDateUtc ?? DateTime.MinValue);
        if (takeIdentity)
        {
            keep.InstitutionId = absorb.InstitutionId; keep.ExternalId = absorb.ExternalId; keep.Name = absorb.Name;
            keep.Balance = absorb.Balance; keep.BalanceDateUtc = absorb.BalanceDateUtc;
        }
        keep.PossibleDuplicateOf = null; keep.DuplicateDismissed = false;
        Items.Remove(absorb);
        foreach (var x in Items.Where(x => x.PossibleDuplicateOf == absorbId)) x.PossibleDuplicateOf = null;
        Merges.Add((keepId, absorbId));
        return Task.FromResult(new AccountMergeResult(0, 0, 0, takeIdentity));
    }
    public Task<long> UpsertAsync(Account a, CancellationToken ct = default)
    {
        var existing = Items.FirstOrDefault(x => x.InstitutionId == a.InstitutionId && x.ExternalId == a.ExternalId);
        if (existing is null) { a.Id = Items.Count + 1; a.CreatedAtUtc = a.CreatedAtUtc == default ? DateTime.UtcNow : a.CreatedAtUtc; Items.Add(a); return Task.FromResult(a.Id); }
        existing.Name = a.Name; existing.Balance = a.Balance; existing.AvailableBalance = a.AvailableBalance; existing.BalanceDateUtc = a.BalanceDateUtc;
        return Task.FromResult(existing.Id);
    }
    public Task SetCategoryAsync(long accountId, long? categoryId, CancellationToken ct = default) { Items.First(a => a.Id == accountId).CategoryId = categoryId; return Task.CompletedTask; }
    public Task SetHiddenAsync(long accountId, bool hidden, CancellationToken ct = default) { Items.First(a => a.Id == accountId).IsHidden = hidden; return Task.CompletedTask; }
    public Task UpsertSnapshotAsync(BalanceSnapshot s, CancellationToken ct = default)
    {
        Snapshots.RemoveAll(x => x.AccountId == s.AccountId && x.AsOf == s.AsOf);
        Snapshots.Add(s);
        return Task.CompletedTask;
    }
    public Task SetDisplayNameAsync(long accountId, string? displayName, CancellationToken ct = default) { Items.First(a => a.Id == accountId).DisplayName = displayName; return Task.CompletedTask; }
    public Task SetExcludeFromNetWorthAsync(long accountId, bool exclude, CancellationToken ct = default) { Items.First(a => a.Id == accountId).ExcludeFromNetWorth = exclude; return Task.CompletedTask; }
    public Task SetOwnerAsync(long accountId, long? ownerId, CancellationToken ct = default) { Items.First(a => a.Id == accountId).OwnerId = ownerId; return Task.CompletedTask; }
    public Task SetPlannedContributionAsync(long accountId, decimal? monthly, CancellationToken ct = default) { Planned[accountId] = monthly; return Task.CompletedTask; }
    public Dictionary<long, decimal?> Planned { get; } = new();
    public Task DeleteAsync(long accountId, CancellationToken ct = default) { Items.RemoveAll(a => a.Id == accountId); return Task.CompletedTask; }
    public List<AccountCategory> Categories { get; } = new();
    public Task<IReadOnlyList<AccountCategory>> ListCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountCategory>>(Categories.OrderBy(c => c.SortOrder).ToList());
    public Task<long> InsertCategoryAsync(AccountCategory c, CancellationToken ct = default) { c.Id = Categories.Count + 1; Categories.Add(c); return Task.FromResult(c.Id); }
    public Task UpdateCategoryAsync(AccountCategory c, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCategoryAsync(long id, CancellationToken ct = default) { Categories.RemoveAll(c => c.Id == id); return Task.CompletedTask; }
    public Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        for (var i = 0; i < orderedIds.Count; i++) Categories.First(c => c.Id == orderedIds[i]).SortOrder = (i + 1) * 10;
        return Task.CompletedTask;
    }
    public Task<int> CountAccountsOfCategoryAsync(long categoryId, CancellationToken ct = default) => Task.FromResult(Items.Count(a => a.CategoryId == categoryId));
}

/// <summary>Mirrors the MySQL unique-key + fingerprint dedupe rules of TransactionRepository.</summary>
public sealed class FakeTransactionRepository : ITransactionRepository
{
    public List<Transaction> Items { get; } = new();

    public Task<UpsertOutcome> UpsertAsync(Transaction t, CancellationToken ct = default)
    {
        if (t.CreatedAtUtc == default) t.CreatedAtUtc = DateTime.UtcNow;
        if (t.ExternalId is null)
        {
            if (Items.Any(x => x.AccountId == t.AccountId && x.Fingerprint == t.Fingerprint)) return Task.FromResult(UpsertOutcome.Unchanged);
            t.Id = Items.Count + 1; Items.Add(t); return Task.FromResult(UpsertOutcome.Inserted);
        }
        var existing = Items.FirstOrDefault(x => x.AccountId == t.AccountId && x.ExternalId == t.ExternalId);
        if (existing is null) { t.Id = Items.Count + 1; Items.Add(t); return Task.FromResult(UpsertOutcome.Inserted); }
        var changed = existing.Amount != t.Amount || existing.Description != t.Description || existing.IsPending != t.IsPending || existing.PostedAtUtc != t.PostedAtUtc;
        existing.Amount = t.Amount; existing.Description = t.Description; existing.IsPending = t.IsPending; existing.PostedAtUtc = t.PostedAtUtc; existing.Fingerprint = t.Fingerprint;
        return Task.FromResult(changed ? UpsertOutcome.Updated : UpsertOutcome.Unchanged);
    }

    public Task<IReadOnlyList<TransactionRow>> ListAsync(long? accountId, DateTime? fromUtc, DateTime? toUtc, int limit = 500, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<TransactionRow>> ListAsync(TransactionFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default) => Task.FromResult(Items.Count);

    public Task<IReadOnlyList<CategorizationCandidate>> ListForCategorizationAsync(bool includeRuleAssigned, bool includeImported, long? institutionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CategorizationCandidate>>(Items
            .Where(t => t.CategorySource != CategorySource.Manual
                        && (includeImported || t.CategorySource != CategorySource.Imported)
                        && (includeRuleAssigned || t.CategorySource == CategorySource.None))
            .Where(t => institutionId is null || AccountInstitution.Count == 0 || AccountInstitution.GetValueOrDefault(t.AccountId) == institutionId)
            .Select(t => new CategorizationCandidate { Id = t.Id, AccountId = t.AccountId, Description = t.Description, Payee = t.Payee, Amount = t.Amount, CategoryId = t.CategoryId, CategorySource = t.CategorySource, CategoryRuleId = t.CategoryRuleId })
            .ToList());

    /// <summary>AccountId → InstitutionId, so institution-scoped categorization can be simulated.</summary>
    public Dictionary<long, long> AccountInstitution { get; } = new();

    public Task SetCategoryAsync(long transactionId, long? categoryId, CategorySource source, long? ruleId, CancellationToken ct = default)
    {
        var t = Items.First(x => x.Id == transactionId);
        t.CategoryId = categoryId; t.CategorySource = source; t.CategoryRuleId = ruleId;
        return Task.CompletedTask;
    }

    public Task SetNotesAsync(long transactionId, string? notes, CancellationToken ct = default) { Items.First(x => x.Id == transactionId).Notes = notes; return Task.CompletedTask; }

    public Task<DateTime?> GetLatestPostedAsync(long institutionId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(t => !t.IsPending).Select(t => (DateTime?)t.PostedAtUtc).DefaultIfEmpty(null).Max());

    public Task<IReadOnlyDictionary<long, int>> CountByAccountAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, int>>(Items.GroupBy(t => t.AccountId).ToDictionary(g => g.Key, g => g.Count()));

    public Task<int> DeleteStalePendingAsync(long accountId, IReadOnlyCollection<string> livePendingExternalIds, CancellationToken ct = default) =>
        Task.FromResult(Items.RemoveAll(t => t.AccountId == accountId && t.IsPending && (t.ExternalId is null || !livePendingExternalIds.Contains(t.ExternalId))));

    public Task<IReadOnlyList<HandoverTransactionRow>> ListForHandoverAsync(long accountId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HandoverTransactionRow>>(Items
            .Where(t => t.AccountId == accountId && t.PostedAtUtc >= fromUtc && t.PostedAtUtc < toUtc)
            .Select(t => new HandoverTransactionRow { Id = t.Id, ExternalId = t.ExternalId, PostedAtUtc = t.PostedAtUtc, Amount = t.Amount, Description = t.Description, CreatedAtUtc = t.CreatedAtUtc, AdoptedFromExternalId = t.AdoptedFromExternalId })
            .ToList());

    public Task<bool> AdoptExternalIdAsync(long transactionId, string externalId, CancellationToken ct = default)
    {
        var t = Items.First(x => x.Id == transactionId);
        if (Items.Any(x => x.Id != t.Id && x.AccountId == t.AccountId && x.ExternalId == externalId))
            return Task.FromResult(false);
        t.AdoptedFromExternalId ??= t.ExternalId ?? "";
        t.ExternalId = externalId;
        return Task.FromResult(true);
    }
}

/// <summary>In-memory stand-in for the handover SQL, working on the fake account and transaction lists.</summary>
public sealed class FakeAccountHandoverRepository(FakeAccountRepository accounts, FakeTransactionRepository transactions, FakeInstitutionRepository institutions) : IAccountHandoverRepository
{
    public List<AccountHandover> Items { get; } = new();

    public Task<IReadOnlyList<AccountHandover>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountHandover>>(Items.ToList());

    public Task HandOverAsync(long accountId, Account identity, CancellationToken ct = default)
    {
        var a = accounts.Items.First(x => x.Id == accountId);
        if (accounts.Items.Any(x => x.Id != accountId && x.InstitutionId == identity.InstitutionId && x.ExternalId == identity.ExternalId))
            throw new InvalidOperationException("taken");
        Items.Add(new AccountHandover
        {
            Id = Items.Count + 1, AccountId = accountId, PreviousInstitutionId = a.InstitutionId, PreviousExternalId = a.ExternalId,
            HandedOverAtUtc = DateTime.UtcNow, HistoryCutoffUtc = LatestPosted(accountId),
        });
        a.InstitutionId = identity.InstitutionId; a.ExternalId = identity.ExternalId; a.Name = identity.Name; a.OrgName = identity.OrgName;
        a.Balance = identity.Balance; a.AvailableBalance = identity.AvailableBalance; a.BalanceDateUtc = identity.BalanceDateUtc; a.PossibleDuplicateOf = null;
        return Task.CompletedTask;
    }

    public Task<int> ReturnAccountsAsync(long institutionId, CancellationToken ct = default)
    {
        var returned = 0;
        foreach (var a in accounts.Items.Where(x => x.InstitutionId == institutionId).ToList())
        {
            var h = Items.Where(x => x.AccountId == a.Id).OrderBy(x => x.HandedOverAtUtc).ThenBy(x => x.Id).LastOrDefault();
            if (h?.PreviousInstitutionId is not { } prev || institutions.Items.All(i => i.Id != prev)) continue;
            a.InstitutionId = prev; a.ExternalId = h.PreviousExternalId!;
            foreach (var t in transactions.Items.Where(t => t.AccountId == a.Id && t.AdoptedFromExternalId is not null))
            {
                t.ExternalId = t.AdoptedFromExternalId == "" ? null : t.AdoptedFromExternalId;
                t.AdoptedFromExternalId = null;
            }
            Items.Remove(h);
            Items.Add(new AccountHandover { Id = Items.Count + 100, AccountId = a.Id, HandedOverAtUtc = DateTime.UtcNow, HistoryCutoffUtc = LatestPosted(a.Id) });
            returned++;
        }
        return Task.FromResult(returned);
    }

    private DateTime? LatestPosted(long accountId) =>
        transactions.Items.Where(t => t.AccountId == accountId && !t.IsPending).Select(t => (DateTime?)t.PostedAtUtc).DefaultIfEmpty(null).Max();
}

public sealed class FakeCategoryRepository : ICategoryRepository
{
    public List<TransactionCategory> Categories { get; } = new();
    public List<CategoryRule> Rules { get; } = new();

    public Task<IReadOnlyList<TransactionCategory>> ListCategoriesAsync(bool includeHidden = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TransactionCategory>>(Categories.Where(c => includeHidden || !c.IsHidden).ToList());
    public Task<long> InsertCategoryAsync(TransactionCategory c, CancellationToken ct = default) { c.Id = Categories.Count + 1; Categories.Add(c); return Task.FromResult(c.Id); }
    public Task UpdateCategoryAsync(TransactionCategory c, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCategoryAsync(long id, CancellationToken ct = default) { Categories.RemoveAll(c => c.Id == id); return Task.CompletedTask; }
    public Task<IReadOnlyList<CategoryRule>> ListRulesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CategoryRule>>(Rules.ToList());
    public Task<IReadOnlyList<CategoryRuleRow>> ListRuleRowsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<long> InsertRuleAsync(CategoryRule r, CancellationToken ct = default) { r.Id = Rules.Count + 1; Rules.Add(r); return Task.FromResult(r.Id); }
    public Task UpdateRuleAsync(CategoryRule r, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteRuleAsync(long id, CancellationToken ct = default) { Rules.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
    public Task RecordRuleHitsAsync(long ruleId, int count, CancellationToken ct = default)
    {
        var r = Rules.First(x => x.Id == ruleId); r.HitCount += count; r.LastHitAtUtc = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}

public sealed class FakeSyncRunRepository : ISyncRunRepository
{
    public List<SyncRun> Runs { get; } = new();
    public Task<long> StartAsync(long institutionId, CancellationToken ct = default)
    {
        var r = new SyncRun { Id = Runs.Count + 1, InstitutionId = institutionId, StartedAtUtc = DateTime.UtcNow, Status = SyncStatus.Running };
        Runs.Add(r); return Task.FromResult(r.Id);
    }
    public Task FinishAsync(SyncRun run, CancellationToken ct = default) { Runs[(int)run.Id - 1] = run; return Task.CompletedTask; }
    public Task<IReadOnlyList<SyncRun>> ListRecentAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SyncRun>>(Runs.ToList());
}

public sealed class FakeProvider(ProviderKind kind = ProviderKind.SimpleFin) : IFinancialProvider
{
    public ProviderKind Kind => kind;
    public Func<FetchRequest, ProviderAccountSet> Response { get; set; } = _ => new ProviderAccountSet([], []);
    public List<FetchRequest> Requests { get; } = new();
    public string? LastCredentialJson { get; private set; }
    public Task<ProviderAccountSet> FetchAsync(string credentialJson, FetchRequest request, CancellationToken ct = default)
    {
        LastCredentialJson = credentialJson;
        Requests.Add(request);
        return Task.FromResult(Response(request));
    }
}
