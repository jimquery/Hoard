using Dapper;
using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Hoard.Core.Security;

namespace Hoard.Core.Data;

public sealed record DataKeyRow(int KeyVersion, byte[] WrappedKey, string ProtectionScope, DateTime CreatedAtUtc);

public interface IDataKeyStore
{
    Task<DataKeyRow?> GetLatestAsync(CancellationToken ct = default);
    Task<DataKeyRow?> GetAsync(int version, CancellationToken ct = default);
    Task<int> InsertAsync(byte[] wrappedKey, string scope, CancellationToken ct = default);
}

public sealed class DataKeyStore : IDataKeyStore
{
    private readonly IDbConnectionFactory _f;
    public DataKeyStore(IDbConnectionFactory f) => _f = f;

    public async Task<DataKeyRow?> GetLatestAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<DataKeyRow>(
            "SELECT key_version AS KeyVersion, wrapped_key AS WrappedKey, protection_scope AS ProtectionScope, created_at_utc AS CreatedAtUtc FROM data_keys ORDER BY key_version DESC LIMIT 1");
    }

    public async Task<DataKeyRow?> GetAsync(int version, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<DataKeyRow>(
            "SELECT key_version AS KeyVersion, wrapped_key AS WrappedKey, protection_scope AS ProtectionScope, created_at_utc AS CreatedAtUtc FROM data_keys WHERE key_version = @version", new { version });
    }

    public async Task<int> InsertAsync(byte[] wrappedKey, string scope, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var next = await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(key_version), 0) + 1 FROM data_keys");
        await c.ExecuteAsync(
            "INSERT INTO data_keys (key_version, wrapped_key, protection_scope, created_at_utc) VALUES (@next, @wrappedKey, @scope, @now)",
            new { next, wrappedKey, scope, now = DateTime.UtcNow });
        return next;
    }
}

public interface IInstitutionRepository
{
    Task<IReadOnlyList<Institution>> ListAsync(CancellationToken ct = default);
    Task<Institution?> GetAsync(long id, CancellationToken ct = default);
    Task<long> InsertAsync(Institution institution, CancellationToken ct = default);
    Task UpdateSyncStateAsync(long id, SyncStatus status, string? error, DateTime? syncedAtUtc, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task SaveCredentialAsync(long institutionId, SealedSecret sealedSecret, CancellationToken ct = default);
    Task<SealedSecret?> GetCredentialAsync(long institutionId, CancellationToken ct = default);
}

public sealed class InstitutionRepository : IInstitutionRepository
{
    private readonly IDbConnectionFactory _f;
    public InstitutionRepository(IDbConnectionFactory f) => _f = f;

    private const string Cols = "id, name, provider, is_enabled, created_at_utc, last_sync_at_utc, last_sync_status, last_error";

    public async Task<IReadOnlyList<Institution>> ListAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<Institution>($"SELECT {Cols} FROM institutions ORDER BY name")).ToList();
    }

    public async Task<Institution?> GetAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Institution>($"SELECT {Cols} FROM institutions WHERE id = @id", new { id });
    }

    public async Task<long> InsertAsync(Institution i, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO institutions (name, provider, is_enabled, created_at_utc, last_sync_status)
            VALUES (@Name, @Provider, @IsEnabled, @CreatedAtUtc, @LastSyncStatus)
            """, i);
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateSyncStateAsync(long id, SyncStatus status, string? error, DateTime? syncedAtUtc, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE institutions
               SET last_sync_status = @status,
                   last_error = @error,
                   last_sync_at_utc = COALESCE(@syncedAtUtc, last_sync_at_utc)
             WHERE id = @id
            """, new { id, status, error, syncedAtUtc });
    }

    public async Task RenameAsync(long id, string name, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE institutions SET name = @name WHERE id = @id", new { id, name = name.Trim() });
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM institutions WHERE id = @id", new { id });
    }

    public async Task SaveCredentialAsync(long institutionId, SealedSecret s, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO institution_credentials (institution_id, key_version, nonce, ciphertext, tag, updated_at_utc)
            VALUES (@institutionId, @KeyVersion, @Nonce, @Ciphertext, @Tag, @now)
            ON DUPLICATE KEY UPDATE key_version = VALUES(key_version), nonce = VALUES(nonce),
                                    ciphertext = VALUES(ciphertext), tag = VALUES(tag), updated_at_utc = VALUES(updated_at_utc)
            """, new { institutionId, s.KeyVersion, s.Nonce, s.Ciphertext, s.Tag, now = DateTime.UtcNow });
    }

    public async Task<SealedSecret?> GetCredentialAsync(long institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SealedSecret>(
            "SELECT key_version AS KeyVersion, nonce AS Nonce, ciphertext AS Ciphertext, tag AS Tag FROM institution_credentials WHERE institution_id = @institutionId",
            new { institutionId });
    }
}

public sealed class AccountSummary
{
    public long Id { get; set; }
    public long InstitutionId { get; set; }
    public string InstitutionName { get; set; } = "";
    /// <summary>Effective name: the user's display name if set, else the bank's name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Name as supplied by the bank.</summary>
    public string BankName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? OrgName { get; set; }
    /// <summary>Provider account id (SimpleFIN ids often end with the account number's last digits).</summary>
    public string? ExternalId { get; set; }
    public string Currency { get; set; } = "USD";
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public AccountKind? CategoryKind { get; set; }
    public string? CategoryColor { get; set; }
    public int CategorySortOrder { get; set; }
    public long? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public decimal Balance { get; set; }
    public decimal? AvailableBalance { get; set; }
    public DateTime? BalanceDateUtc { get; set; }
    public bool IsHidden { get; set; }
    public bool ExcludeFromNetWorth { get; set; }
    /// <summary>The existing account this one looks like a second copy of (see <see cref="Account.PossibleDuplicateOf"/>).</summary>
    public long? PossibleDuplicateOf { get; set; }
}

public interface IAccountRepository
{
    Task<IReadOnlyList<AccountSummary>> ListSummariesAsync(bool includeHidden = false, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> ListByInstitutionAsync(long institutionId, CancellationToken ct = default);
    /// <summary>Every account row, hidden ones included, across all institutions.</summary>
    Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken ct = default);
    /// <summary>Insert or update by (institution_id, external_id). Returns the account id.</summary>
    Task<long> UpsertAsync(Account account, CancellationToken ct = default);
    /// <summary>Flags (or, with null, un-flags) an account as a probable duplicate of another; <paramref name="dismissed"/> records "not a duplicate".</summary>
    Task SetPossibleDuplicateAsync(long accountId, long? ofAccountId, bool dismissed, CancellationToken ct = default);
    /// <summary>
    /// Folds <paramref name="absorbId"/> into <paramref name="keepId"/> in one transaction: re-homes its transactions
    /// (dropping the ones the survivor already has, see <see cref="TransactionMergePlanner"/>), snapshots, rule scopes,
    /// property links and import mappings; fills user settings the survivor lacks; and, when the absorbed row is the
    /// one the provider currently feeds, hands its provider identity to the survivor so future syncs land there.
    /// The absorbed account is then deleted.
    /// </summary>
    Task<AccountMergeResult> MergeAsync(long keepId, long absorbId, int dedupeWindowDays, CancellationToken ct = default);
    Task SetCategoryAsync(long accountId, long? categoryId, CancellationToken ct = default);
    Task SetHiddenAsync(long accountId, bool hidden, CancellationToken ct = default);
    /// <summary>Null or blank clears the override and reverts to the bank-supplied name.</summary>
    Task SetDisplayNameAsync(long accountId, string? displayName, CancellationToken ct = default);
    Task SetExcludeFromNetWorthAsync(long accountId, bool exclude, CancellationToken ct = default);
    Task SetOwnerAsync(long accountId, long? ownerId, CancellationToken ct = default);
    /// <summary>Planned monthly contribution used by projections; null clears it (fall back to history).</summary>
    Task SetPlannedContributionAsync(long accountId, decimal? monthly, CancellationToken ct = default);
    /// <summary>Deletes an account with its transactions and snapshots (FK cascade).</summary>
    Task DeleteAsync(long accountId, CancellationToken ct = default);
    Task UpsertSnapshotAsync(BalanceSnapshot snapshot, CancellationToken ct = default);

    Task<IReadOnlyList<AccountCategory>> ListCategoriesAsync(CancellationToken ct = default);
    Task<long> InsertCategoryAsync(AccountCategory category, CancellationToken ct = default);
    Task UpdateCategoryAsync(AccountCategory category, CancellationToken ct = default);
    /// <summary>Accounts of this type become untyped (FK ON DELETE SET NULL).</summary>
    Task DeleteCategoryAsync(long id, CancellationToken ct = default);
    /// <summary>Sets sort_order = index * 10 for the given id order.</summary>
    Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);
    Task<int> CountAccountsOfCategoryAsync(long categoryId, CancellationToken ct = default);
}

public sealed class AccountRepository : IAccountRepository
{
    private readonly IDbConnectionFactory _f;
    public AccountRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<AccountSummary>> ListSummariesAsync(bool includeHidden = false, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountSummary>("""
            SELECT a.id, a.institution_id, i.name AS institution_name,
                   COALESCE(NULLIF(a.display_name, ''), a.name) AS name, a.name AS bank_name, a.display_name, a.org_name, a.external_id, a.currency,
                   a.category_id, cat.name AS category_name, cat.kind AS category_kind, cat.color AS category_color,
                   COALESCE(cat.sort_order, 9999) AS category_sort_order, a.owner_id, o.name AS owner_name,
                   a.balance, a.available_balance, a.balance_date_utc, a.is_hidden, a.exclude_from_net_worth, a.possible_duplicate_of
              FROM accounts a
              JOIN institutions i ON i.id = a.institution_id
              LEFT JOIN account_categories cat ON cat.id = a.category_id
              LEFT JOIN household_members o ON o.id = a.owner_id
             WHERE (@includeHidden = 1 OR a.is_hidden = 0)
             ORDER BY i.name, COALESCE(NULLIF(a.display_name, ''), a.name)
            """, new { includeHidden = includeHidden ? 1 : 0 })).ToList();
    }

    public async Task<IReadOnlyList<Account>> ListByInstitutionAsync(long institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<Account>(
            "SELECT * FROM accounts WHERE institution_id = @institutionId ORDER BY name", new { institutionId })).ToList();
    }

    public async Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<Account>("SELECT * FROM accounts ORDER BY institution_id, name, id")).ToList();
    }

    public async Task SetPossibleDuplicateAsync(long accountId, long? ofAccountId, bool dismissed, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET possible_duplicate_of = @ofAccountId, duplicate_dismissed = @dismissed WHERE id = @accountId",
            new { accountId, ofAccountId, dismissed });
    }

    public async Task<AccountMergeResult> MergeAsync(long keepId, long absorbId, int dedupeWindowDays, CancellationToken ct = default)
    {
        if (keepId == absorbId) throw new ArgumentException("An account cannot be merged into itself.");
        using var c = await _f.OpenAsync(ct);
        using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        var keep = await c.QuerySingleOrDefaultAsync<Account>("SELECT * FROM accounts WHERE id = @id FOR UPDATE", new { id = keepId }, tx)
            ?? throw new InvalidOperationException($"Account {keepId} no longer exists.");
        var absorb = await c.QuerySingleOrDefaultAsync<Account>("SELECT * FROM accounts WHERE id = @id FOR UPDATE", new { id = absorbId }, tx)
            ?? throw new InvalidOperationException($"Account {absorbId} no longer exists.");

        // 1. Transactions: move what the survivor lacks; what it already has stays behind and is deleted with the account.
        const string rowsSql = "SELECT id, external_id, posted_at_utc, amount, description FROM transactions WHERE account_id = @id";
        var keepRows = (await c.QueryAsync<MergeTransactionRow>(rowsSql, new { id = keepId }, tx)).ToList();
        var absorbRows = (await c.QueryAsync<MergeTransactionRow>(rowsSql, new { id = absorbId }, tx)).ToList();
        var plan = TransactionMergePlanner.Plan(keepRows, absorbRows, dedupeWindowDays);
        var absorbById = absorbRows.ToDictionary(r => r.Id);
        foreach (var id in plan.Move)
        {
            var r = absorbById[id];
            // The fingerprint embeds the account id, so it has to be recomputed for the new home.
            await c.ExecuteAsync("UPDATE transactions SET account_id = @keepId, fingerprint = @fingerprint, updated_at_utc = @now WHERE id = @id",
                new { keepId, fingerprint = TransactionFingerprint.Compute(keepId, r.PostedAtUtc, r.Amount, r.Description), now, id }, tx);
        }

        // 2. Everything else that points at the absorbed account. UPDATE IGNORE skips snapshot days the survivor already has.
        var snapshotsMoved = await c.ExecuteAsync("UPDATE IGNORE balance_snapshots SET account_id = @keepId WHERE account_id = @absorbId", new { keepId, absorbId }, tx);
        await c.ExecuteAsync("UPDATE category_rules SET account_id = @keepId WHERE account_id = @absorbId", new { keepId, absorbId }, tx);
        await c.ExecuteAsync("UPDATE properties SET account_id = @keepId WHERE account_id = @absorbId", new { keepId, absorbId }, tx);
        await c.ExecuteAsync("UPDATE import_mappings SET target_id = @keepId WHERE kind = @kind AND target_id = @absorbId",
            new { keepId, absorbId, kind = (int)ImportMappingKind.Account }, tx);

        // 3. User settings the survivor lacks come across; its own choices are never overwritten.
        await c.ExecuteAsync("""
            UPDATE accounts k
              JOIN accounts a ON a.id = @absorbId
               SET k.display_name                 = COALESCE(NULLIF(k.display_name, ''), a.display_name),
                   k.category_id                  = COALESCE(k.category_id, a.category_id),
                   k.owner_id                     = COALESCE(k.owner_id, a.owner_id),
                   k.planned_monthly_contribution = COALESCE(k.planned_monthly_contribution, a.planned_monthly_contribution),
                   k.possible_duplicate_of        = NULL,
                   k.duplicate_dismissed          = 0,
                   k.updated_at_utc               = @now
             WHERE k.id = @keepId
            """, new { keepId, absorbId, now }, tx);

        // 4. Whichever copy the provider fed most recently carries the live identity; the survivor takes it so the
        //    next sync upserts into the survivor instead of re-creating the duplicate.
        var takeIdentity = (absorb.BalanceDateUtc ?? DateTime.MinValue) > (keep.BalanceDateUtc ?? DateTime.MinValue);
        if (takeIdentity)
        {
            // Free the unique (institution_id, external_id) slot first.
            await c.ExecuteAsync("UPDATE accounts SET external_id = CONCAT('merged:', id) WHERE id = @absorbId", new { absorbId }, tx);
            await c.ExecuteAsync("""
                UPDATE accounts
                   SET institution_id = @InstitutionId, external_id = @ExternalId, name = @Name, org_name = @OrgName, org_domain = @OrgDomain,
                       currency = @Currency, balance = @Balance, available_balance = @AvailableBalance, balance_date_utc = @BalanceDateUtc,
                       updated_at_utc = @now
                 WHERE id = @keepId
                """, new { absorb.InstitutionId, absorb.ExternalId, absorb.Name, absorb.OrgName, absorb.OrgDomain, absorb.Currency,
                          absorb.Balance, absorb.AvailableBalance, absorb.BalanceDateUtc, now, keepId }, tx);
        }

        // 5. The absorbed row goes, taking its remaining (duplicate) transactions and skipped snapshots with it (FK cascade).
        await c.ExecuteAsync("DELETE FROM accounts WHERE id = @absorbId", new { absorbId }, tx);
        await tx.CommitAsync(ct);
        return new AccountMergeResult(plan.Move.Count, plan.Drop.Count, snapshotsMoved, takeIdentity);
    }

    public async Task<long> UpsertAsync(Account a, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        // Category is intentionally not touched on update: it is user-owned data.
        await c.ExecuteAsync("""
            INSERT INTO accounts (institution_id, external_id, name, org_name, org_domain, currency,
                                  balance, available_balance, balance_date_utc, created_at_utc, updated_at_utc)
            VALUES (@InstitutionId, @ExternalId, @Name, @OrgName, @OrgDomain, @Currency,
                    @Balance, @AvailableBalance, @BalanceDateUtc, @now, @now)
            ON DUPLICATE KEY UPDATE
                name = VALUES(name), org_name = VALUES(org_name), org_domain = VALUES(org_domain),
                currency = VALUES(currency), balance = VALUES(balance), available_balance = VALUES(available_balance),
                balance_date_utc = VALUES(balance_date_utc), updated_at_utc = VALUES(updated_at_utc),
                id = LAST_INSERT_ID(id)
            """, new
        {
            a.InstitutionId, a.ExternalId, a.Name, a.OrgName, a.OrgDomain, a.Currency,
            a.Balance, a.AvailableBalance, a.BalanceDateUtc, now
        });
        // LAST_INSERT_ID(id) in the UPDATE branch makes this return the existing row's id on a duplicate.
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task SetCategoryAsync(long accountId, long? categoryId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET category_id = @categoryId, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, categoryId, now = DateTime.UtcNow });
    }

    public async Task SetHiddenAsync(long accountId, bool hidden, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET is_hidden = @hidden, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, hidden, now = DateTime.UtcNow });
    }

    public async Task SetDisplayNameAsync(long accountId, string? displayName, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET display_name = @displayName, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(), now = DateTime.UtcNow });
    }

    public async Task SetExcludeFromNetWorthAsync(long accountId, bool exclude, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET exclude_from_net_worth = @exclude, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, exclude, now = DateTime.UtcNow });
    }

    public async Task SetOwnerAsync(long accountId, long? ownerId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET owner_id = @ownerId, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, ownerId, now = DateTime.UtcNow });
    }

    public async Task SetPlannedContributionAsync(long accountId, decimal? monthly, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE accounts SET planned_monthly_contribution = @monthly, updated_at_utc = @now WHERE id = @accountId",
            new { accountId, monthly, now = DateTime.UtcNow });
    }

    public async Task DeleteAsync(long accountId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM accounts WHERE id = @accountId", new { accountId });
    }

    public async Task UpsertSnapshotAsync(BalanceSnapshot s, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO balance_snapshots (account_id, as_of, balance, available_balance)
            VALUES (@AccountId, @AsOf, @Balance, @AvailableBalance)
            ON DUPLICATE KEY UPDATE balance = VALUES(balance), available_balance = VALUES(available_balance)
            """, new { s.AccountId, AsOf = s.AsOf.ToDateTime(TimeOnly.MinValue), s.Balance, s.AvailableBalance });
    }

    public async Task<IReadOnlyList<AccountCategory>> ListCategoriesAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountCategory>("SELECT id, name, kind, sort_order, color, is_system, include_in_spending FROM account_categories ORDER BY sort_order, name")).ToList();
    }

    public async Task<long> InsertCategoryAsync(AccountCategory cat, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var sort = await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(sort_order), 0) + 10 FROM account_categories");
        await c.ExecuteAsync("INSERT INTO account_categories (name, kind, sort_order, color, is_system, include_in_spending) VALUES (@Name, @Kind, @sort, @Color, 0, @IncludeInSpending)",
            new { cat.Name, cat.Kind, sort, cat.Color, cat.IncludeInSpending });
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateCategoryAsync(AccountCategory cat, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE account_categories SET name = @Name, kind = @Kind, color = @Color, include_in_spending = @IncludeInSpending WHERE id = @Id", cat);
    }

    public async Task DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM account_categories WHERE id = @id", new { id });
    }

    public async Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        for (var i = 0; i < orderedIds.Count; i++)
            await c.ExecuteAsync("UPDATE account_categories SET sort_order = @sort WHERE id = @id", new { id = orderedIds[i], sort = (i + 1) * 10 });
    }

    public async Task<int> CountAccountsOfCategoryAsync(long categoryId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM accounts WHERE category_id = @categoryId", new { categoryId });
    }
}

public sealed class TransactionRow
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string InstitutionName { get; set; } = "";
    public DateTime PostedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? Payee { get; set; }
    public string? Memo { get; set; }
    public string? Notes { get; set; }
    public bool IsPending { get; set; }
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? ParentCategoryName { get; set; }
    public CategorySource CategorySource { get; set; }

    public string CategoryPath => CategoryName is null ? "" : ParentCategoryName is null ? CategoryName : $"{ParentCategoryName}: {CategoryName}";
}

/// <summary>Filter for the transactions list. All members optional.</summary>
public sealed class TransactionFilter
{
    public long? AccountId { get; set; }
    public long? InstitutionId { get; set; }
    /// <summary>Restrict to accounts of this account type.</summary>
    public long? AccountTypeId { get; set; }
    /// <summary>Restrict to accounts that have no account type.</summary>
    public bool UntypedAccountsOnly { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    /// <summary>Matches a category or any of its children.</summary>
    public long? CategoryId { get; set; }
    public bool UncategorizedOnly { get; set; }
    public string? Text { get; set; }
    public int Limit { get; set; } = 500;
    public int Offset { get; set; }
}

/// <summary>Slim projection used by the categorization engine.</summary>
public sealed class CategorizationCandidate
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Description { get; set; } = "";
    public string? Payee { get; set; }
    public decimal Amount { get; set; }
    public long? CategoryId { get; set; }
    public CategorySource CategorySource { get; set; }
    public long? CategoryRuleId { get; set; }
}

public enum UpsertOutcome { Inserted, Updated, Unchanged }

public interface ITransactionRepository
{
    Task<UpsertOutcome> UpsertAsync(Transaction tx, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionRow>> ListAsync(long? accountId, DateTime? fromUtc, DateTime? toUtc, int limit = 500, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionRow>> ListAsync(TransactionFilter filter, CancellationToken ct = default);
    Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default);
    /// <summary>
    /// Transactions the rule engine may touch. Manual choices are never returned. <paramref name="includeRuleAssigned"/>
    /// adds rows a rule already claimed (re-evaluation); <paramref name="includeImported"/> adds rows carrying a
    /// category that came from a CSV file, so a manual run or a new rule can override it.
    /// </summary>
    Task<IReadOnlyList<CategorizationCandidate>> ListForCategorizationAsync(bool includeRuleAssigned, bool includeImported, long? institutionId, CancellationToken ct = default);
    Task SetCategoryAsync(long transactionId, long? categoryId, CategorySource source, long? ruleId, CancellationToken ct = default);
    Task SetNotesAsync(long transactionId, string? notes, CancellationToken ct = default);
    Task<DateTime?> GetLatestPostedAsync(long institutionId, CancellationToken ct = default);
    /// <summary>Number of stored transactions per account id (accounts with none are absent).</summary>
    Task<IReadOnlyDictionary<long, int>> CountByAccountAsync(CancellationToken ct = default);
    /// <summary>Removes pending transactions for an account that are absent from the latest provider response.</summary>
    Task<int> DeleteStalePendingAsync(long accountId, IReadOnlyCollection<string> livePendingExternalIds, CancellationToken ct = default);
}

public sealed class TransactionRepository : ITransactionRepository
{
    private readonly IDbConnectionFactory _f;
    public TransactionRepository(IDbConnectionFactory f) => _f = f;

    public async Task<UpsertOutcome> UpsertAsync(Transaction t, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;

        if (t.ExternalId is null)
        {
            // No provider id: fall back to the fingerprint. Never update by fingerprint (it is not unique by design).
            var exists = await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM transactions WHERE account_id = @AccountId AND fingerprint = @Fingerprint",
                new { t.AccountId, t.Fingerprint });
            if (exists > 0) return UpsertOutcome.Unchanged;
            await c.ExecuteAsync("""
                INSERT INTO transactions (account_id, external_id, posted_at_utc, transacted_at_utc, amount, description,
                                          payee, memo, is_pending, fingerprint, created_at_utc, updated_at_utc)
                VALUES (@AccountId, NULL, @PostedAtUtc, @TransactedAtUtc, @Amount, @Description,
                        @Payee, @Memo, @IsPending, @Fingerprint, @now, @now)
                """, new { t.AccountId, t.PostedAtUtc, t.TransactedAtUtc, t.Amount, t.Description, t.Payee, t.Memo, t.IsPending, t.Fingerprint, now });
            return UpsertOutcome.Inserted;
        }

        // With UseAffectedRows=true in the connection string (see SettingsStore.BuildConnectionString),
        // MySQL reports 1 for an insert, 2 for an update that changed something, and 0 when the row was identical.
        var affected = await c.ExecuteAsync("""
            INSERT INTO transactions (account_id, external_id, posted_at_utc, transacted_at_utc, amount, description,
                                      payee, memo, is_pending, fingerprint, created_at_utc, updated_at_utc)
            VALUES (@AccountId, @ExternalId, @PostedAtUtc, @TransactedAtUtc, @Amount, @Description,
                    @Payee, @Memo, @IsPending, @Fingerprint, @now, @now)
            ON DUPLICATE KEY UPDATE
                posted_at_utc = VALUES(posted_at_utc), transacted_at_utc = VALUES(transacted_at_utc),
                amount = VALUES(amount), description = VALUES(description), payee = VALUES(payee), memo = VALUES(memo),
                is_pending = VALUES(is_pending), fingerprint = VALUES(fingerprint),
                updated_at_utc = IF(posted_at_utc <=> VALUES(posted_at_utc) AND amount <=> VALUES(amount)
                                    AND description <=> VALUES(description) AND payee <=> VALUES(payee)
                                    AND memo <=> VALUES(memo) AND is_pending <=> VALUES(is_pending),
                                    updated_at_utc, VALUES(updated_at_utc))
            """, new { t.AccountId, t.ExternalId, t.PostedAtUtc, t.TransactedAtUtc, t.Amount, t.Description, t.Payee, t.Memo, t.IsPending, t.Fingerprint, now });

        return affected switch
        {
            1 => UpsertOutcome.Inserted,
            2 => UpsertOutcome.Updated,
            _ => UpsertOutcome.Unchanged,
        };
    }

    public Task<IReadOnlyList<TransactionRow>> ListAsync(long? accountId, DateTime? fromUtc, DateTime? toUtc, int limit = 500, CancellationToken ct = default) =>
        ListAsync(new TransactionFilter { AccountId = accountId, FromUtc = fromUtc, ToUtc = toUtc, Limit = limit }, ct);

    private const string FilterWhere = """
             WHERE (@AccountId IS NULL OR t.account_id = @AccountId)
               AND (@InstitutionId IS NULL OR a.institution_id = @InstitutionId)
               AND (@AccountTypeId IS NULL OR a.category_id = @AccountTypeId)
               AND (@UntypedAccountsOnly = 0 OR a.category_id IS NULL)
               AND (@FromUtc IS NULL OR t.posted_at_utc >= @FromUtc)
               AND (@ToUtc IS NULL OR t.posted_at_utc < @ToUtc)
               AND (@CategoryId IS NULL OR t.category_id = @CategoryId OR c.parent_id = @CategoryId)
               AND (@UncategorizedOnly = 0 OR t.category_id IS NULL)
               AND (@TextLike IS NULL OR t.description LIKE @TextLike OR t.payee LIKE @TextLike OR t.memo LIKE @TextLike OR t.notes LIKE @TextLike)
        """;

    private static object FilterParams(TransactionFilter f) => new
    {
        f.AccountId, f.InstitutionId, f.AccountTypeId, f.FromUtc, f.ToUtc, f.CategoryId,
        UntypedAccountsOnly = f.UntypedAccountsOnly ? 1 : 0,
        UncategorizedOnly = f.UncategorizedOnly ? 1 : 0,
        TextLike = string.IsNullOrWhiteSpace(f.Text) ? null : "%" + f.Text.Trim() + "%",
        Limit = Math.Clamp(f.Limit, 1, 10000),
        Offset = Math.Max(0, f.Offset),
    };

    public async Task<IReadOnlyList<TransactionRow>> ListAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<TransactionRow>($"""
            SELECT t.id, t.account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS account_name, i.name AS institution_name,
                   t.posted_at_utc, t.amount, t.description, t.payee, t.memo, t.notes, t.is_pending,
                   t.category_id, c.name AS category_name, p.name AS parent_category_name, t.category_source
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
              JOIN institutions i ON i.id = a.institution_id
              LEFT JOIN transaction_categories c ON c.id = t.category_id
              LEFT JOIN transaction_categories p ON p.id = c.parent_id
            {FilterWhere}
             ORDER BY t.posted_at_utc DESC, t.id DESC
             LIMIT @Limit OFFSET @Offset
            """, FilterParams(filter))).ToList();
    }

    public async Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
              LEFT JOIN transaction_categories c ON c.id = t.category_id
            {FilterWhere}
            """, FilterParams(filter));
    }

    public async Task<IReadOnlyList<CategorizationCandidate>> ListForCategorizationAsync(bool includeRuleAssigned, bool includeImported, long? institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<CategorizationCandidate>("""
            SELECT t.id, t.account_id, t.description, t.payee, t.amount, t.category_id, t.category_source, t.category_rule_id
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
             WHERE t.category_source <> @manual
               AND (@includeImported = 1 OR t.category_source <> @imported)
               AND (@includeRuleAssigned = 1 OR t.category_source = @none)
               AND (@institutionId IS NULL OR a.institution_id = @institutionId)
            """, new { manual = CategorySource.Manual, imported = CategorySource.Imported, none = CategorySource.None,
                      includeRuleAssigned = includeRuleAssigned ? 1 : 0, includeImported = includeImported ? 1 : 0, institutionId })).ToList();
    }

    public async Task SetCategoryAsync(long transactionId, long? categoryId, CategorySource source, long? ruleId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE transactions
               SET category_id = @categoryId, category_source = @source, category_rule_id = @ruleId, updated_at_utc = @now
             WHERE id = @transactionId
            """, new { transactionId, categoryId, source, ruleId, now = DateTime.UtcNow });
    }

    public async Task SetNotesAsync(long transactionId, string? notes, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE transactions SET notes = @notes, updated_at_utc = @now WHERE id = @transactionId",
            new { transactionId, notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyDictionary<long, int>> CountByAccountAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var rows = await c.QueryAsync<(long AccountId, int Count)>("SELECT account_id, COUNT(*) FROM transactions GROUP BY account_id");
        return rows.ToDictionary(r => r.AccountId, r => r.Count);
    }

    public async Task<DateTime?> GetLatestPostedAsync(long institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.ExecuteScalarAsync<DateTime?>("""
            SELECT MAX(t.posted_at_utc) FROM transactions t
              JOIN accounts a ON a.id = t.account_id
             WHERE a.institution_id = @institutionId AND t.is_pending = 0
            """, new { institutionId });
    }

    public async Task<int> DeleteStalePendingAsync(long accountId, IReadOnlyCollection<string> livePendingExternalIds, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        if (livePendingExternalIds.Count == 0)
            return await c.ExecuteAsync("DELETE FROM transactions WHERE account_id = @accountId AND is_pending = 1", new { accountId });
        return await c.ExecuteAsync(
            "DELETE FROM transactions WHERE account_id = @accountId AND is_pending = 1 AND external_id NOT IN @ids",
            new { accountId, ids = livePendingExternalIds });
    }
}

public interface ISyncRunRepository
{
    Task<long> StartAsync(long institutionId, CancellationToken ct = default);
    Task FinishAsync(SyncRun run, CancellationToken ct = default);
    Task<IReadOnlyList<SyncRun>> ListRecentAsync(int limit = 50, CancellationToken ct = default);
}

public sealed class SyncRunRepository : ISyncRunRepository
{
    private readonly IDbConnectionFactory _f;
    public SyncRunRepository(IDbConnectionFactory f) => _f = f;

    public async Task<long> StartAsync(long institutionId, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync(
            "INSERT INTO sync_runs (institution_id, started_at_utc, status) VALUES (@institutionId, @now, @status)",
            new { institutionId, now = DateTime.UtcNow, status = SyncStatus.Running });
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task FinishAsync(SyncRun r, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE sync_runs SET finished_at_utc = @FinishedAtUtc, status = @Status, accounts_seen = @AccountsSeen,
                   transactions_added = @TransactionsAdded, transactions_updated = @TransactionsUpdated, error = @Error
             WHERE id = @Id
            """, r);
    }

    public async Task<IReadOnlyList<SyncRun>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<SyncRun>("SELECT * FROM sync_runs ORDER BY started_at_utc DESC LIMIT @limit", new { limit })).ToList();
    }
}
