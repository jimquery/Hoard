using Hoard.Core.Data;
using Hoard.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>A flagged pair: the account that was there first and the one a later sync (or scan) took for a second copy of it.</summary>
public sealed record DuplicateAccountPair(
    Account Existing, string ExistingInstitution, int ExistingTransactions,
    Account Newcomer, string NewcomerInstitution, int NewcomerTransactions);

/// <summary>
/// Finds, lists, merges and dismisses probable duplicate accounts. Detection at sync time lives in
/// <see cref="SyncService"/>; <see cref="ScanAsync"/> applies the same heuristic to what is already stored, for
/// duplicates that were created before Hoard started flagging them.
/// </summary>
public sealed class AccountMergeService
{
    /// <summary>A transaction of the absorbed account is the survivor's if the same amount and description posted within this many days.</summary>
    public const int DedupeWindowDays = 3;

    private readonly IAccountRepository _accounts;
    private readonly ITransactionRepository _transactions;
    private readonly IInstitutionRepository _institutions;
    private readonly ILogger<AccountMergeService> _log;

    public AccountMergeService(IAccountRepository accounts, ITransactionRepository transactions, IInstitutionRepository institutions, ILogger<AccountMergeService> log)
    {
        _accounts = accounts;
        _transactions = transactions;
        _institutions = institutions;
        _log = log;
    }

    /// <summary>Pairs currently flagged for review, oldest existing account first.</summary>
    public async Task<IReadOnlyList<DuplicateAccountPair>> ListFlaggedAsync(CancellationToken ct = default)
    {
        var all = await _accounts.ListAllAsync(ct);
        var byId = all.ToDictionary(a => a.Id);
        var institutions = (await _institutions.ListAsync(ct)).ToDictionary(i => i.Id, i => i.Name);
        var counts = await _transactions.CountByAccountAsync(ct);

        var pairs = new List<DuplicateAccountPair>();
        foreach (var newcomer in all.Where(a => a.PossibleDuplicateOf is not null))
        {
            if (!byId.TryGetValue(newcomer.PossibleDuplicateOf!.Value, out var existing)) continue; // twin already gone; FK clears the flag next time
            pairs.Add(new DuplicateAccountPair(
                existing, institutions.GetValueOrDefault(existing.InstitutionId, ""), counts.GetValueOrDefault(existing.Id),
                newcomer, institutions.GetValueOrDefault(newcomer.InstitutionId, ""), counts.GetValueOrDefault(newcomer.Id)));
        }
        return pairs.OrderBy(p => p.Existing.CreatedAtUtc).ThenBy(p => p.Existing.Id).ToList();
    }

    /// <summary>
    /// Applies the duplicate heuristic to every stored account and flags the newer half of each matching pair that is
    /// not already flagged or dismissed. Returns everything flagged afterwards.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateAccountPair>> ScanAsync(CancellationToken ct = default)
    {
        var all = (await _accounts.ListAllAsync(ct)).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToList();
        var flagged = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var older = all[i];
            for (var j = i + 1; j < all.Count; j++)
            {
                var newer = all[j];
                if (newer.PossibleDuplicateOf is not null || newer.DuplicateDismissed) continue;
                if (!AccountDuplicateDetector.LooksLikeDuplicate(older, newer)) continue;
                await _accounts.SetPossibleDuplicateAsync(newer.Id, older.Id, dismissed: false, ct);
                newer.PossibleDuplicateOf = older.Id;
                flagged++;
            }
        }
        _log.LogInformation("Duplicate-account scan flagged {Flagged} account(s)", flagged);
        return await ListFlaggedAsync(ct);
    }

    /// <summary>Merges <paramref name="absorbId"/> into <paramref name="keepId"/>. Irreversible.</summary>
    public async Task<AccountMergeResult> MergeAsync(long keepId, long absorbId, CancellationToken ct = default)
    {
        var result = await _accounts.MergeAsync(keepId, absorbId, DedupeWindowDays, ct);
        _log.LogInformation("Merged account {Absorb} into {Keep}: {Moved} transactions moved, {Dropped} duplicates dropped, {Snapshots} snapshots moved, identity taken: {Identity}",
            absorbId, keepId, result.TransactionsMoved, result.TransactionsDropped, result.SnapshotsMoved, result.IdentityTaken);
        return result;
    }

    /// <summary>"Not a duplicate": clears the flag and remembers the answer so a later scan leaves the account alone.</summary>
    public Task DismissAsync(long newcomerId, CancellationToken ct = default) =>
        _accounts.SetPossibleDuplicateAsync(newcomerId, null, dismissed: true, ct);
}
