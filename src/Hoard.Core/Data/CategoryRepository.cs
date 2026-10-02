using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Data;

/// <summary>A rule joined with its category names, for display in the rules editor.</summary>
public sealed class CategoryRuleRow
{
    public long Id { get; set; }
    public int Priority { get; set; }
    public RuleMatchField MatchField { get; set; }
    public RuleMatchType MatchType { get; set; }
    public string Pattern { get; set; } = "";
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public long? AccountId { get; set; }
    public string? AccountName { get; set; }
    public long CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string? ParentCategoryName { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsSystem { get; set; }
    public int HitCount { get; set; }
    public DateTime? LastHitAtUtc { get; set; }

    public string CategoryPath => ParentCategoryName is null ? CategoryName : $"{ParentCategoryName}: {CategoryName}";
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<TransactionCategory>> ListCategoriesAsync(bool includeHidden = false, CancellationToken ct = default);
    Task<long> InsertCategoryAsync(TransactionCategory category, CancellationToken ct = default);
    Task UpdateCategoryAsync(TransactionCategory category, CancellationToken ct = default);
    /// <summary>Deletes a category. Transactions referencing it become uncategorized; rules referencing it are deleted (FK cascade).</summary>
    Task DeleteCategoryAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<CategoryRule>> ListRulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CategoryRuleRow>> ListRuleRowsAsync(CancellationToken ct = default);
    Task<long> InsertRuleAsync(CategoryRule rule, CancellationToken ct = default);
    Task UpdateRuleAsync(CategoryRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(long id, CancellationToken ct = default);
    Task RecordRuleHitsAsync(long ruleId, int count, CancellationToken ct = default);
}

public sealed class CategoryRepository : ICategoryRepository
{
    private readonly IDbConnectionFactory _f;
    public CategoryRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<TransactionCategory>> ListCategoriesAsync(bool includeHidden = false, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<TransactionCategory>("""
            SELECT id, parent_id, name, kind, sort_order, is_system, is_hidden
              FROM transaction_categories
             WHERE (@includeHidden = 1 OR is_hidden = 0)
             ORDER BY parent_id IS NOT NULL, sort_order, name
            """, new { includeHidden = includeHidden ? 1 : 0 })).ToList();
    }

    public async Task<long> InsertCategoryAsync(TransactionCategory cat, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO transaction_categories (parent_id, name, kind, sort_order, is_system, is_hidden)
            VALUES (@ParentId, @Name, @Kind, @SortOrder, 0, @IsHidden)
            """, cat);
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateCategoryAsync(TransactionCategory cat, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE transaction_categories
               SET parent_id = @ParentId, name = @Name, kind = @Kind, sort_order = @SortOrder, is_hidden = @IsHidden
             WHERE id = @Id
            """, cat);
    }

    public async Task DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM transaction_categories WHERE id = @id", new { id });
    }

    private const string RuleCols = """
        id, priority, match_field, match_type, pattern, amount_min, amount_max, account_id, category_id,
        is_enabled, is_system, hit_count, last_hit_at_utc, created_at_utc, updated_at_utc
        """;

    public async Task<IReadOnlyList<CategoryRule>> ListRulesAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<CategoryRule>($"SELECT {RuleCols} FROM category_rules ORDER BY priority, id")).ToList();
    }

    public async Task<IReadOnlyList<CategoryRuleRow>> ListRuleRowsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<CategoryRuleRow>("""
            SELECT r.id, r.priority, r.match_field, r.match_type, r.pattern, r.amount_min, r.amount_max,
                   r.account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS account_name, r.category_id, c.name AS category_name, p.name AS parent_category_name,
                   r.is_enabled, r.is_system, r.hit_count, r.last_hit_at_utc
              FROM category_rules r
              JOIN transaction_categories c ON c.id = r.category_id
              LEFT JOIN transaction_categories p ON p.id = c.parent_id
              LEFT JOIN accounts a ON a.id = r.account_id
             ORDER BY r.priority, r.id
            """)).ToList();
    }

    public async Task<long> InsertRuleAsync(CategoryRule r, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        await c.ExecuteAsync("""
            INSERT INTO category_rules (priority, match_field, match_type, pattern, amount_min, amount_max, account_id, category_id,
                                        is_enabled, is_system, created_at_utc, updated_at_utc)
            VALUES (@Priority, @MatchField, @MatchType, @Pattern, @AmountMin, @AmountMax, @AccountId, @CategoryId,
                    @IsEnabled, 0, @now, @now)
            """, new { r.Priority, r.MatchField, r.MatchType, r.Pattern, r.AmountMin, r.AmountMax, r.AccountId, r.CategoryId, r.IsEnabled, now });
        return await c.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()");
    }

    public async Task UpdateRuleAsync(CategoryRule r, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE category_rules
               SET priority = @Priority, match_field = @MatchField, match_type = @MatchType, pattern = @Pattern,
                   amount_min = @AmountMin, amount_max = @AmountMax, account_id = @AccountId, category_id = @CategoryId,
                   is_enabled = @IsEnabled, updated_at_utc = @now
             WHERE id = @Id
            """, new { r.Id, r.Priority, r.MatchField, r.MatchType, r.Pattern, r.AmountMin, r.AmountMax, r.AccountId, r.CategoryId, r.IsEnabled, now = DateTime.UtcNow });
    }

    public async Task DeleteRuleAsync(long id, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM category_rules WHERE id = @id", new { id });
    }

    public async Task RecordRuleHitsAsync(long ruleId, int count, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE category_rules SET hit_count = hit_count + @count, last_hit_at_utc = @now WHERE id = @ruleId",
            new { ruleId, count, now = DateTime.UtcNow });
    }
}
