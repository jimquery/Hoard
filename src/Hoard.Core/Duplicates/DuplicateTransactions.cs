using System.Text.RegularExpressions;
using Dapper;
using Hoard.Core.Categorization;
using Hoard.Core.Data;

namespace Hoard.Core.Duplicates;

/// <summary>A stored transaction as the possible-duplicates report sees it.</summary>
public sealed class DuplicateCandidateRow
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string InstitutionName { get; set; } = "";
    public DateTime PostedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? Payee { get; set; }
    public string? CategoryPath { get; set; }
    /// <summary>Null when the row came from a file import (no provider id).</summary>
    public string? ExternalId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDuplicate { get; set; }
}

/// <summary>Transactions that look like the same purchase recorded more than once.</summary>
public sealed class DuplicateGroup
{
    public int Number { get; init; }
    public IReadOnlyList<DuplicateCandidateRow> Rows { get; init; } = [];
    /// <summary>The row to keep if the others are duplicates: the provider-synced one, else the first stored.</summary>
    public long SuggestedKeepId { get; init; }
    /// <summary>True when every row but one is already marked a duplicate.</summary>
    public bool IsResolved => Rows.Count(r => !r.IsDuplicate) <= 1;
}

/// <summary>
/// Finds possible duplicates: two or more transactions on the same account, for the same amount, from the same vendor,
/// posted within <c>windowDays</c> of each other. Vendors match on <see cref="MerchantKey"/> (so "SQ *BLUE BOTTLE 0412"
/// and "Blue Bottle Coffee" agree) or when one key is the start of the other ("AMAZON" / "AMAZON MKTPLACE").
/// Matches chain: A~B and B~C put A, B and C in one group. Pairs the user dismissed never link.
/// </summary>
public static partial class DuplicateFinder
{
    [GeneratedRegex(@"\b(?:CHECK|CHECK\s*NO|CHK|CHEQUE)\s*[#:]?\s*(\d{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex CheckNumber();

    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<DuplicateCandidateRow> rows, int windowDays,
        IReadOnlySet<(long, long)>? dismissed = null, bool includeResolved = false)
    {
        windowDays = Math.Max(0, windowDays);
        var parent = rows.ToDictionary(r => r.Id, r => r.Id);
        long Root(long id) { while (parent[id] != id) id = parent[id] = parent[parent[id]]; return id; }

        foreach (var bucket in rows.GroupBy(r => (r.AccountId, r.Amount)))
        {
            var ordered = bucket.OrderBy(r => r.PostedAtUtc).ThenBy(r => r.Id).ToList();
            if (ordered.Count < 2) continue;
            var keys = ordered.ToDictionary(r => r.Id, VendorKeys);
            for (var i = 0; i < ordered.Count; i++)
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];
                    if ((b.PostedAtUtc.Date - a.PostedAtUtc.Date).TotalDays > windowDays) break;
                    if (dismissed is not null && dismissed.Contains(Pair(a.Id, b.Id))) continue;
                    if (!SameVendor(keys[a.Id], keys[b.Id])) continue;
                    if (DifferentCheckNumbers(a.Description, b.Description)) continue;
                    parent[Root(b.Id)] = Root(a.Id);
                }
        }

        var groups = rows.GroupBy(r => Root(r.Id)).Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(r => r.PostedAtUtc).ThenBy(r => r.Id).ToList())
            .OrderByDescending(g => g[0].PostedAtUtc).ThenBy(g => g[0].AccountName)
            .ToList();

        var result = new List<DuplicateGroup>();
        foreach (var g in groups)
        {
            var group = new DuplicateGroup { Rows = g, SuggestedKeepId = SuggestKeep(g) };
            if (!includeResolved && group.IsResolved) continue;
            result.Add(new DuplicateGroup { Number = result.Count + 1, Rows = g, SuggestedKeepId = group.SuggestedKeepId });
        }
        return result;
    }

    /// <summary>A dismissal key: the pair with the smaller id first.</summary>
    public static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

    /// <summary>
    /// Keep a row already kept (not marked) if there is exactly one; otherwise prefer a provider-synced row (it has an id the
    /// next sync will update) over a file import, then the one stored first.
    /// </summary>
    public static long SuggestKeep(IReadOnlyList<DuplicateCandidateRow> rows)
    {
        var unmarked = rows.Where(r => !r.IsDuplicate).ToList();
        var pool = unmarked.Count > 0 ? unmarked : rows.ToList();
        return pool.OrderBy(r => r.ExternalId is null ? 1 : 0).ThenBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).First().Id;
    }

    private static string[] VendorKeys(DuplicateCandidateRow r) =>
        new[] { MerchantKey.Extract(r.Description, r.Payee), MerchantKey.Extract(r.Description) }
            .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>"CHECK 190" and "CHECK 193" for the same amount are two checks, not one recorded twice.</summary>
    public static bool DifferentCheckNumbers(string a, string b)
    {
        var x = CheckNumber().Match(a);
        var y = CheckNumber().Match(b);
        return x.Success && y.Success && x.Groups[1].Value.TrimStart('0') != y.Groups[1].Value.TrimStart('0');
    }

    public static bool SameVendor(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (var x in a)
            foreach (var y in b)
                if (x == y || StartsWithWord(x, y) || StartsWithWord(y, x)) return true;
        return false;
    }

    /// <summary>"AMAZON MKTPLACE" starts with the word "AMAZON"; "AMAZONIA" does not. Short keys never prefix-match.</summary>
    private static bool StartsWithWord(string longer, string shorter) =>
        shorter.Length >= 4 && longer.Length > shorter.Length
        && longer.StartsWith(shorter, StringComparison.Ordinal) && longer[shorter.Length] == ' ';
}

public interface IDuplicateRepository
{
    /// <summary>Posted (not pending) transactions in visible accounts since <paramref name="fromUtc"/>, including ones already marked.</summary>
    Task<IReadOnlyList<DuplicateCandidateRow>> ListCandidatesAsync(DateTime? fromUtc, CancellationToken ct = default);
    Task<IReadOnlySet<(long, long)>> ListDismissedPairsAsync(CancellationToken ct = default);
    /// <summary>Marks (or unmarks) transactions as duplicates in one statement each. Returns how many rows changed.</summary>
    Task<int> SetDuplicateAsync(IReadOnlyCollection<long> transactionIds, bool isDuplicate, CancellationToken ct = default);
    /// <summary>Records that every pair among <paramref name="transactionIds"/> is not a duplicate.</summary>
    Task DismissAsync(IReadOnlyCollection<long> transactionIds, CancellationToken ct = default);
}

public sealed class DuplicateRepository : IDuplicateRepository
{
    private readonly IDbConnectionFactory _f;
    public DuplicateRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<DuplicateCandidateRow>> ListCandidatesAsync(DateTime? fromUtc, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<DuplicateCandidateRow>("""
            SELECT t.id, t.account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS account_name, i.name AS institution_name,
                   t.posted_at_utc, t.amount, t.description, t.payee, t.external_id, t.created_at_utc, t.is_duplicate,
                   CASE WHEN c.id IS NULL THEN NULL WHEN p.id IS NULL THEN c.name ELSE CONCAT(p.name, ': ', c.name) END AS category_path
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
              JOIN institutions i ON i.id = a.institution_id
              LEFT JOIN transaction_categories c ON c.id = t.category_id
              LEFT JOIN transaction_categories p ON p.id = c.parent_id
             WHERE t.is_pending = 0 AND a.is_hidden = 0
               AND (@fromUtc IS NULL OR t.posted_at_utc >= @fromUtc)
            """, new { fromUtc })).ToList();
    }

    public async Task<IReadOnlySet<(long, long)>> ListDismissedPairsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var rows = await c.QueryAsync<(long A, long B)>("SELECT transaction_id, other_transaction_id FROM transaction_duplicate_dismissals");
        return rows.Select(r => DuplicateFinder.Pair(r.A, r.B)).ToHashSet();
    }

    public async Task<int> SetDuplicateAsync(IReadOnlyCollection<long> transactionIds, bool isDuplicate, CancellationToken ct = default)
    {
        if (transactionIds.Count == 0) return 0;
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        var changed = 0;
        // MySQL caps the IN list only by packet size; chunk anyway so a huge bulk save stays well inside it.
        foreach (var chunk in transactionIds.Distinct().Chunk(500))
            changed += await c.ExecuteAsync("""
                UPDATE transactions
                   SET is_duplicate = @flag, duplicate_marked_at_utc = IF(@flag = 1, @now, NULL), updated_at_utc = @now
                 WHERE id IN @ids AND is_duplicate <> @flag
                """, new { ids = chunk, flag = isDuplicate ? 1 : 0, now });
        return changed;
    }

    public async Task DismissAsync(IReadOnlyCollection<long> transactionIds, CancellationToken ct = default)
    {
        var ids = transactionIds.Distinct().OrderBy(i => i).ToList();
        if (ids.Count < 2) return;
        using var c = await _f.OpenAsync(ct);
        var now = DateTime.UtcNow;
        var pairs = new List<object>();
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
                pairs.Add(new { a = ids[i], b = ids[j], now });
        await c.ExecuteAsync("""
            INSERT IGNORE INTO transaction_duplicate_dismissals (transaction_id, other_transaction_id, dismissed_at_utc)
            VALUES (@a, @b, @now)
            """, pairs);
    }
}

/// <summary>The possible-duplicates report and the bulk mark/unmark behind it.</summary>
public sealed class DuplicateService
{
    public const int DefaultWindowDays = 2;
    private readonly IDuplicateRepository _repo;
    public DuplicateService(IDuplicateRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(DateTime? fromUtc, int windowDays = DefaultWindowDays, bool includeResolved = false, CancellationToken ct = default)
    {
        var rows = await _repo.ListCandidatesAsync(fromUtc, ct);
        var dismissed = await _repo.ListDismissedPairsAsync(ct);
        return DuplicateFinder.Find(rows, windowDays, dismissed, includeResolved);
    }

    /// <summary>Applies the user's choices in bulk. Returns (marked, unmarked).</summary>
    public async Task<(int Marked, int Unmarked)> SaveAsync(IReadOnlyCollection<long> markIds, IReadOnlyCollection<long> unmarkIds, CancellationToken ct = default)
    {
        var marked = await _repo.SetDuplicateAsync(markIds, true, ct);
        var unmarked = await _repo.SetDuplicateAsync(unmarkIds, false, ct);
        return (marked, unmarked);
    }

    public Task DismissAsync(IReadOnlyCollection<long> transactionIds, CancellationToken ct = default) => _repo.DismissAsync(transactionIds, ct);

    public Task<int> SetDuplicateAsync(IReadOnlyCollection<long> transactionIds, bool isDuplicate, CancellationToken ct = default) =>
        _repo.SetDuplicateAsync(transactionIds, isDuplicate, ct);
}
