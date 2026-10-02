using System.Text.RegularExpressions;
using Hoard.Core.Domain;

namespace Hoard.Core.Sync;

/// <summary>What one account merge did.</summary>
/// <param name="TransactionsMoved">Transactions re-homed to the surviving account.</param>
/// <param name="TransactionsDropped">Transactions of the absorbed account that the survivor already had (deleted with it).</param>
/// <param name="SnapshotsMoved">Daily balance snapshots re-homed; days the survivor already had are dropped.</param>
/// <param name="IdentityTaken">The survivor took over the absorbed account's provider identity (institution, external id, bank name, balance) because that was the copy the provider currently feeds.</param>
public sealed record AccountMergeResult(int TransactionsMoved, int TransactionsDropped, int SnapshotsMoved, bool IdentityTaken);

/// <summary>
/// Decides whether two account rows are probably the same real-world account seen through two provider ids.
/// A reconnect at the aggregator typically re-issues every id while the bank's own name, currency and org stay
/// the same, so that is the signal: same bank (org) or same Hoard institution, same bank-reported name, same
/// currency, different external id. Balances are deliberately not required to match — a transaction posting between
/// the two connections would otherwise hide a real duplicate. False positives (two identically named accounts at one
/// bank) are cheap: the user dismisses the pair once.
/// </summary>
public static partial class AccountDuplicateDetector
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? "" : Whitespace().Replace(s.Trim(), " ").ToUpperInvariant();

    public static bool LooksLikeDuplicate(Account a, Account b)
    {
        if (a.Id == b.Id) return false;
        if (a.InstitutionId == b.InstitutionId && string.Equals(a.ExternalId, b.ExternalId, StringComparison.Ordinal)) return false; // the same row
        if (!string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Normalize(a.Name);
        if (name.Length == 0 || name != Normalize(b.Name)) return false;
        var orgA = Normalize(a.OrgName);
        var orgB = Normalize(b.OrgName);
        // Same bank as the provider reports it; when either side has no org, fall back to "same Hoard institution".
        return orgA.Length > 0 && orgB.Length > 0 ? orgA == orgB : a.InstitutionId == b.InstitutionId;
    }

    /// <summary>
    /// The existing account a newly inserted one most likely duplicates, or null. Prefers a match in the same
    /// institution, then an equal balance, then the copy the provider fed most recently.
    /// </summary>
    public static Account? FindMatch(Account newcomer, IEnumerable<Account> existing) =>
        existing.Where(e => LooksLikeDuplicate(newcomer, e))
                .OrderByDescending(e => e.InstitutionId == newcomer.InstitutionId)
                .ThenByDescending(e => e.Balance == newcomer.Balance)
                .ThenByDescending(e => e.BalanceDateUtc ?? DateTime.MinValue)
                .FirstOrDefault();
}

/// <summary>Slim view of a stored transaction, enough to decide whether two rows are the same bank transaction.</summary>
public sealed class MergeTransactionRow
{
    public long Id { get; set; }
    public string? ExternalId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}

/// <param name="Move">Ids of the absorbed account's transactions to re-home to the survivor.</param>
/// <param name="Drop">Ids the survivor already has an equivalent of; they go away with the absorbed account.</param>
public sealed record TransactionMergePlan(IReadOnlyList<long> Move, IReadOnlyList<long> Drop);

/// <summary>
/// Pure planner for folding one account's transactions into another. The two connections usually overlap for the
/// initial history window, so a row of the absorbed account is a duplicate when the survivor has a row with
/// (1) the same provider id, or (2) the same posted date, amount and normalized description, or (3) the same amount
/// and description posted within <c>windowDays</c> (a pending row that posted under a shifted date). Each survivor
/// row can absorb one duplicate, nearest date first. The survivor's copy is the one kept, so its categories and
/// notes are never lost.
/// </summary>
public static class TransactionMergePlanner
{
    public static TransactionMergePlan Plan(IReadOnlyList<MergeTransactionRow> keep, IReadOnlyList<MergeTransactionRow> absorb, int windowDays)
    {
        var keepExternal = keep.Where(k => !string.IsNullOrEmpty(k.ExternalId)).Select(k => k.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var byKey = keep.GroupBy(k => (k.Amount, TransactionFingerprint.NormalizeDescription(k.Description)))
                        .ToDictionary(g => g.Key, g => g.Select(k => new Candidate(k.PostedAtUtc.Date)).ToList());

        var move = new List<long>();
        var drop = new List<long>();
        foreach (var t in absorb.OrderBy(t => t.PostedAtUtc).ThenBy(t => t.Id))
        {
            if (!string.IsNullOrEmpty(t.ExternalId) && keepExternal.Contains(t.ExternalId)) { drop.Add(t.Id); continue; }
            if (byKey.TryGetValue((t.Amount, TransactionFingerprint.NormalizeDescription(t.Description)), out var candidates)
                && TakeNearest(candidates, t.PostedAtUtc.Date, windowDays))
            {
                drop.Add(t.Id);
                continue;
            }
            move.Add(t.Id);
        }
        return new TransactionMergePlan(move, drop);
    }

    private sealed class Candidate(DateTime date) { public DateTime Date { get; } = date; public bool Taken { get; set; } }

    private static bool TakeNearest(List<Candidate> list, DateTime date, int windowDays)
    {
        Candidate? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in list)
        {
            if (c.Taken) continue;
            var distance = Math.Abs((c.Date - date).TotalDays);
            if (distance <= windowDays && distance < bestDistance) { best = c; bestDistance = distance; }
        }
        if (best is null) return false;
        best.Taken = true;
        return true;
    }
}
