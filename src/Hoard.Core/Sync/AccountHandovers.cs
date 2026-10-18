using Hoard.Core.Domain;
using Hoard.Core.Providers;

namespace Hoard.Core.Sync;

/// <summary>Slim view of a stored transaction for matching a new connection's download against an account's old rows.</summary>
public sealed class HandoverTransactionRow
{
    public long Id { get; set; }
    public string? ExternalId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string? AdoptedFromExternalId { get; set; }
}

/// <param name="ExternalId">The incoming provider transaction id.</param>
/// <param name="TransactionId">The stored row that is the same bank transaction and takes over that id.</param>
public sealed record HandoverMatch(string ExternalId, long TransactionId);

/// <summary>
/// Pure matcher for the overlap after an account changes connection. The new provider's first download repeats
/// history the account already has, under different ids and often different wording, so its transactions are matched
/// against the rows that existed at the handover: same amount (both providers' signs are normalized to Hoard's),
/// posted within <c>windowDays</c>, nearest date first, a matching normalized description preferred. Each old row is
/// matched at most once. Only posted incoming transactions dated no later than the cutoff plus the window are
/// considered (later ones are new activity), and an incoming id the account already stores is left to the normal upsert.
/// A matched row takes the new id (the caller adopts it), so it keeps its category and notes and later syncs update it.
/// </summary>
public static class HandoverMatcher
{
    public static IReadOnlyList<HandoverMatch> Match(
        IReadOnlyList<ProviderTransaction> incoming,
        IReadOnlyList<HandoverTransactionRow> stored,
        AccountHandover handover,
        int windowDays)
    {
        var matches = new List<HandoverMatch>();
        if (handover.HistoryCutoffUtc is not { } cutoff) return matches; // the account had no history: nothing to overlap

        var knownIds = stored.Where(s => !string.IsNullOrEmpty(s.ExternalId)).Select(s => s.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var incomingIds = incoming.Where(t => !string.IsNullOrEmpty(t.ExternalId)).Select(t => t.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var candidates = stored
            .Where(s => s.CreatedAtUtc <= handover.HandedOverAtUtc && s.AdoptedFromExternalId is null
                        && (s.ExternalId is null || !incomingIds.Contains(s.ExternalId)))
            .ToList();
        var taken = new HashSet<long>();
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        var last = cutoff.Date.AddDays(windowDays);

        foreach (var t in incoming.Where(t => !t.IsPending && !string.IsNullOrEmpty(t.ExternalId) && !knownIds.Contains(t.ExternalId!) && t.PostedAtUtc.Date <= last)
                                  .OrderBy(t => t.PostedAtUtc).ThenBy(t => t.ExternalId, StringComparer.Ordinal))
        {
            // An id repeated in the download is one transaction: matching it twice would give two rows the same id.
            if (matchedIds.Contains(t.ExternalId!)) continue;
            var description = TransactionFingerprint.NormalizeDescription(t.Description);
            var best = candidates
                .Where(c => !taken.Contains(c.Id) && c.Amount == t.Amount && Math.Abs((c.PostedAtUtc.Date - t.PostedAtUtc.Date).TotalDays) <= windowDays)
                .OrderByDescending(c => TransactionFingerprint.NormalizeDescription(c.Description) == description)
                .ThenBy(c => Math.Abs((c.PostedAtUtc.Date - t.PostedAtUtc.Date).TotalDays))
                .ThenBy(c => c.Id)
                .FirstOrDefault();
            if (best is null) continue;
            taken.Add(best.Id);
            matchedIds.Add(t.ExternalId!);
            matches.Add(new HandoverMatch(t.ExternalId!, best.Id));
        }
        return matches;
    }
}

/// <summary>An account a new connection reports, as offered for linking to an existing Hoard account.</summary>
public sealed record LinkableAccount(string ExternalId, string Name, string? Mask, string? OrgName, string Currency, decimal? Balance);

/// <summary>
/// Suggests which existing Hoard account a newly connected account is, so the link dialog can pre-select it. Signals:
/// the account number's last digits (Plaid's mask; SimpleFIN ids and names usually end with them), the bank, the
/// name and the balance. A suggestion needs the digits or two other signals, and each existing account is suggested
/// for at most one new account (best score first). The user always confirms or changes the choice.
/// </summary>
public static class AccountLinkSuggester
{
    public static IReadOnlyDictionary<string, long> Suggest(IReadOnlyList<LinkableAccount> incoming, IReadOnlyList<Account> existing)
    {
        var scored = new List<(string ExternalId, long AccountId, int Score)>();
        foreach (var n in incoming)
            foreach (var e in existing)
                if (Score(n, e) is var s and >= 3) scored.Add((n.ExternalId, e.Id, s));

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var used = new HashSet<long>();
        foreach (var (externalId, accountId, _) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.AccountId))
        {
            if (result.ContainsKey(externalId) || used.Contains(accountId)) continue;
            result[externalId] = accountId;
            used.Add(accountId);
        }
        return result;
    }

    public static int Score(LinkableAccount n, Account e)
    {
        if (!string.Equals(n.Currency, e.Currency, StringComparison.OrdinalIgnoreCase)) return 0;
        var score = 0;
        if (!string.IsNullOrWhiteSpace(n.Mask) && n.Mask.Length >= 2
            && (e.ExternalId.EndsWith(n.Mask, StringComparison.Ordinal) || e.Name.Contains(n.Mask, StringComparison.Ordinal)
                || (e.DisplayName?.Contains(n.Mask, StringComparison.Ordinal) ?? false)))
            score += 3;

        var orgN = AccountDuplicateDetector.Normalize(n.OrgName);
        var orgE = AccountDuplicateDetector.Normalize(e.OrgName);
        if (orgN.Length > 0 && orgE.Length > 0 && (orgN.Contains(orgE) || orgE.Contains(orgN))) score += 1;

        var nameN = StripMask(AccountDuplicateDetector.Normalize(n.Name), n.Mask);
        var nameE = AccountDuplicateDetector.Normalize(e.Name);
        if (nameN.Length > 0 && nameE.Length > 0)
        {
            if (nameN == nameE) score += 2;
            else if (nameE.Contains(nameN) || nameN.Contains(nameE)) score += 1;
        }

        if (n.Balance is { } b && b == e.Balance) score += 2;
        return score;
    }

    private static string StripMask(string name, string? mask) =>
        string.IsNullOrWhiteSpace(mask) ? name : name.Replace($"({mask})", "", StringComparison.Ordinal).Trim();
}
