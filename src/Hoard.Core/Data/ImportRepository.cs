using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Data;

public enum ImportMappingKind { Account = 1, Category = 2 }

public sealed class ImportMapping
{
    public string Source { get; set; } = "";
    public ImportMappingKind Kind { get; set; }
    public string SourceKey { get; set; } = "";
    public long? TargetId { get; set; }
}

/// <summary>Slim view of stored transactions used to spot duplicates during an import.</summary>
public sealed class DedupeRow
{
    public long Id { get; set; }
    public string? ExternalId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}

public interface IImportRepository
{
    Task<IReadOnlyList<ImportMapping>> ListMappingsAsync(string source, CancellationToken ct = default);
    Task SaveMappingsAsync(IEnumerable<ImportMapping> mappings, CancellationToken ct = default);
    Task<IReadOnlyList<DedupeRow>> ListForDedupeAsync(long accountId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    /// <summary>
    /// Inserts an imported transaction with its category (from a rule, or from the file when no rule matched).
    /// On a re-import of the same row (same account + external id) the category is refreshed only if the user has
    /// not since changed it by hand.
    /// </summary>
    Task<UpsertOutcome> InsertImportedAsync(Transaction tx, CancellationToken ct = default);
}

public sealed class ImportRepository : IImportRepository
{
    private readonly IDbConnectionFactory _f;
    public ImportRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<ImportMapping>> ListMappingsAsync(string source, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<ImportMapping>("SELECT source, kind, source_key, target_id FROM import_mappings WHERE source = @source", new { source })).ToList();
    }

    public async Task SaveMappingsAsync(IEnumerable<ImportMapping> mappings, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        foreach (var m in mappings)
            await c.ExecuteAsync("""
                INSERT INTO import_mappings (source, kind, source_key, target_id, updated_at_utc)
                VALUES (@Source, @Kind, @SourceKey, @TargetId, @now)
                ON DUPLICATE KEY UPDATE target_id = VALUES(target_id), updated_at_utc = VALUES(updated_at_utc)
                """, new { m.Source, m.Kind, m.SourceKey, m.TargetId, now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyList<DedupeRow>> ListForDedupeAsync(long accountId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<DedupeRow>("""
            SELECT id, external_id, posted_at_utc, amount, description
              FROM transactions
             WHERE account_id = @accountId AND posted_at_utc >= @fromUtc AND posted_at_utc < @toUtc
            """, new { accountId, fromUtc, toUtc })).ToList();
    }

    public async Task<UpsertOutcome> InsertImportedAsync(Transaction t, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        var affected = await c.ExecuteAsync("""
            INSERT INTO transactions (account_id, external_id, posted_at_utc, transacted_at_utc, amount, description, payee, memo, notes,
                                      is_pending, fingerprint, category_id, category_source, category_rule_id, created_at_utc, updated_at_utc)
            VALUES (@AccountId, @ExternalId, @PostedAtUtc, @TransactedAtUtc, @Amount, @Description, @Payee, @Memo, @Notes,
                    0, @Fingerprint, @CategoryId, @CategorySource, @CategoryRuleId, @now, @now)
            ON DUPLICATE KEY UPDATE
                -- Assignments run left to right: decide "changed?" from the stored values first, then apply.
                -- A category the user set by hand (source 2 = Manual) is never replaced by a re-import.
                -- category_source is assigned last because the two above test it.
                updated_at_utc = IF(category_source = @manual OR (category_id <=> VALUES(category_id) AND category_rule_id <=> VALUES(category_rule_id)),
                                    updated_at_utc, VALUES(updated_at_utc)),
                category_id = IF(category_source = @manual, category_id, VALUES(category_id)),
                category_rule_id = IF(category_source = @manual, category_rule_id, VALUES(category_rule_id)),
                category_source = IF(category_source = @manual, category_source, VALUES(category_source))
            """, new { t.AccountId, t.ExternalId, t.PostedAtUtc, t.TransactedAtUtc, t.Amount, t.Description, t.Payee, t.Memo, t.Notes, t.Fingerprint, t.CategoryId, t.CategoryRuleId, CategorySource = (int)t.CategorySource, manual = (int)CategorySource.Manual, now });
        return affected switch { 1 => UpsertOutcome.Inserted, 2 => UpsertOutcome.Updated, _ => UpsertOutcome.Unchanged };
    }
}
