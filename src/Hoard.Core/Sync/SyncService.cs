using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Security;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <param name="PossibleDuplicates">Accounts this run inserted that look like second copies of accounts Hoard already had (flagged for review, not merged).</param>
public sealed record SyncResult(long InstitutionId, SyncStatus Status, int AccountsSeen, int TransactionsAdded, int TransactionsUpdated, string? Error, int PossibleDuplicates = 0);

/// <summary>
/// Orchestrates one refresh of an institution: unseal credentials, call the provider, upsert accounts,
/// dedupe and upsert transactions, record a daily balance snapshot, and log the run.
/// Both the desktop app ("Sync now") and the Windows service call this.
///
/// An account the provider reports under an id Hoard has not seen is inserted as usual, but it is then compared with
/// every existing account (<see cref="AccountDuplicateDetector"/>): a reconnect at the aggregator re-issues ids, and
/// without this every such reconnect silently doubled the account list. A probable duplicate is flagged
/// (<see cref="Account.PossibleDuplicateOf"/>) rather than merged here — the service runs unattended and merging is
/// irreversible — and the desktop offers the merge. Flagging keeps the data: the new row's transactions are folded
/// into the survivor when the user merges.
///
/// Accounts can also change connection on purpose (<see cref="AccountHandover"/>, e.g. a SimpleFIN account linked to
/// Plaid): the old connection skips the provider account it handed over, and the new connection's download is matched
/// against the history the account already had (<see cref="HandoverMatcher"/>) so the overlap is not duplicated.
/// </summary>
public sealed class SyncService
{
    private readonly IInstitutionRepository _institutions;
    private readonly IAccountRepository _accounts;
    private readonly ITransactionRepository _transactions;
    private readonly ISyncRunRepository _runs;
    private readonly IAccountHandoverRepository _handovers;
    private readonly CredentialVault _vault;
    private readonly CategorizationService _categorization;
    private readonly IReadOnlyDictionary<ProviderKind, IFinancialProvider> _providers;
    private readonly SyncSettings _settings;
    private readonly ILogger<SyncService> _log;

    public SyncService(
        IInstitutionRepository institutions,
        IAccountRepository accounts,
        ITransactionRepository transactions,
        ISyncRunRepository runs,
        IAccountHandoverRepository handovers,
        CredentialVault vault,
        CategorizationService categorization,
        IEnumerable<IFinancialProvider> providers,
        SyncSettings settings,
        ILogger<SyncService> log)
    {
        _institutions = institutions;
        _accounts = accounts;
        _transactions = transactions;
        _runs = runs;
        _handovers = handovers;
        _vault = vault;
        _categorization = categorization;
        _providers = providers.ToDictionary(p => p.Kind);
        _settings = settings;
        _log = log;
    }

    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        var results = new List<SyncResult>();
        foreach (var inst in await _institutions.ListAsync(ct))
        {
            if (!inst.IsEnabled) continue;
            ct.ThrowIfCancellationRequested();
            results.Add(await SyncInstitutionAsync(inst.Id, ct));
        }
        return results;
    }

    public async Task<SyncResult> SyncInstitutionAsync(long institutionId, CancellationToken ct = default)
    {
        var inst = await _institutions.GetAsync(institutionId, ct)
            ?? throw new InvalidOperationException($"Institution {institutionId} not found.");
        if (inst.Provider == ProviderKind.Imported)
            return new SyncResult(institutionId, SyncStatus.Succeeded, 0, 0, 0, null); // history-only accounts: nothing to fetch
        var runId = await _runs.StartAsync(institutionId, ct);
        var run = new SyncRun { Id = runId, InstitutionId = institutionId, StartedAtUtc = DateTime.UtcNow, Status = SyncStatus.Running };
        var possibleDuplicates = 0;
        await _institutions.UpdateSyncStateAsync(institutionId, SyncStatus.Running, null, null, ct);
        _log.LogInformation("Sync started for {Institution} ({Id})", inst.Name, inst.Id);

        try
        {
            if (!_providers.TryGetValue(inst.Provider, out var provider))
                throw new ProviderException($"No provider registered for {inst.Provider}.");

            var sealedCred = await _institutions.GetCredentialAsync(institutionId, ct)
                ?? throw new ProviderException("No credentials stored for this institution.", isAuthFailure: true);
            var credentialJson = await _vault.OpenAsync(institutionId, sealedCred, ct);

            var request = await BuildFetchRequestAsync(inst, ct);
            var set = await provider.FetchAsync(credentialJson, request, ct);

            // Snapshot of the accounts that existed before this run, so a new provider id can be spotted and compared.
            var existing = await _accounts.ListAllAsync(ct);
            var knownExternalIds = existing.Where(a => a.InstitutionId == institutionId).Select(a => a.ExternalId).ToHashSet(StringComparer.Ordinal);

            // Provider accounts this connection handed to another one, and the latest handover of each account it received.
            var handovers = await _handovers.ListAsync(ct);
            var handedAway = handovers.Where(h => h.PreviousInstitutionId == institutionId && h.PreviousExternalId is not null)
                                      .Select(h => h.PreviousExternalId!).ToHashSet(StringComparer.Ordinal);
            var latestHandover = handovers.GroupBy(h => h.AccountId)
                                          .ToDictionary(g => g.Key, g => g.OrderBy(h => h.HandedOverAtUtc).ThenBy(h => h.Id).Last());

            foreach (var pa in set.Accounts)
            {
                ct.ThrowIfCancellationRequested();
                if (handedAway.Contains(pa.ExternalId) && !knownExternalIds.Contains(pa.ExternalId))
                {
                    _log.LogDebug("Sync for {Institution} skipped account {ExternalId}: it was handed over to another connection", inst.Name, pa.ExternalId);
                    continue;
                }
                var account = new Account
                {
                    InstitutionId = institutionId,
                    ExternalId = pa.ExternalId,
                    Name = pa.Name,
                    OrgName = pa.OrgName,
                    OrgDomain = pa.OrgDomain,
                    Currency = pa.Currency,
                    Balance = pa.Balance,
                    AvailableBalance = pa.AvailableBalance,
                    BalanceDateUtc = pa.BalanceDateUtc,
                };
                var accountId = await _accounts.UpsertAsync(account, ct);
                run.AccountsSeen++;

                if (!knownExternalIds.Contains(pa.ExternalId))
                {
                    account.Id = accountId;
                    if (AccountDuplicateDetector.FindMatch(account, existing) is { } twin)
                    {
                        await _accounts.SetPossibleDuplicateAsync(accountId, twin.Id, dismissed: false, ct);
                        possibleDuplicates++;
                        _log.LogWarning("Sync for {Institution} inserted account {Name} ({ExternalId}) which looks like a duplicate of account {TwinId} ({TwinExternalId}); flagged for review",
                            inst.Name, pa.Name, pa.ExternalId, twin.Id, twin.ExternalId);
                    }
                }

                await _accounts.UpsertSnapshotAsync(new BalanceSnapshot
                {
                    AccountId = accountId,
                    AsOf = DateOnly.FromDateTime(pa.BalanceDateUtc ?? DateTime.UtcNow),
                    Balance = pa.Balance,
                    AvailableBalance = pa.AvailableBalance,
                }, ct);

                if (latestHandover.TryGetValue(accountId, out var handover))
                    await AdoptHandedOverHistoryAsync(accountId, handover, pa.Transactions, ct);

                var livePending = new List<string>();
                foreach (var pt in pa.Transactions)
                {
                    var outcome = await _transactions.UpsertAsync(new Transaction
                    {
                        AccountId = accountId,
                        ExternalId = string.IsNullOrEmpty(pt.ExternalId) ? null : pt.ExternalId,
                        PostedAtUtc = pt.PostedAtUtc,
                        TransactedAtUtc = pt.TransactedAtUtc,
                        Amount = pt.Amount,
                        Description = pt.Description,
                        Payee = pt.Payee,
                        Memo = pt.Memo,
                        IsPending = pt.IsPending,
                        Fingerprint = TransactionFingerprint.Compute(accountId, pt.PostedAtUtc, pt.Amount, pt.Description),
                    }, ct);
                    if (outcome == UpsertOutcome.Inserted) run.TransactionsAdded++;
                    else if (outcome == UpsertOutcome.Updated) run.TransactionsUpdated++;
                    if (pt.IsPending && !string.IsNullOrEmpty(pt.ExternalId)) livePending.Add(pt.ExternalId);
                }

                // Pending transactions that vanished from the feed have either posted (under a new id, already
                // upserted above) or been cancelled. Either way the stale pending row is a duplicate; drop it.
                if (request.IncludePending)
                    await _transactions.DeleteStalePendingAsync(accountId, livePending, ct);
            }

            // Auto-categorize whatever is new. Manual categories are never touched.
            if (run.TransactionsAdded > 0)
                await _categorization.CategorizeNewAsync(institutionId, ct);

            run.Status = set.Errors.Count == 0 ? SyncStatus.Succeeded : SyncStatus.PartialSuccess;
            run.Error = set.Errors.Count == 0 ? null : string.Join("\n", set.Errors);
            run.FinishedAtUtc = DateTime.UtcNow;
            await _runs.FinishAsync(run, ct);
            await _institutions.UpdateSyncStateAsync(institutionId, run.Status, run.Error, run.FinishedAtUtc, ct);
            _log.LogInformation("Sync finished for {Institution}: {Accounts} accounts, +{Added}/~{Updated} transactions, status {Status}",
                inst.Name, run.AccountsSeen, run.TransactionsAdded, run.TransactionsUpdated, run.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Sync failed for {Institution}", inst.Name);
            run.Status = SyncStatus.Failed;
            run.Error = ex.Message;
            run.FinishedAtUtc = DateTime.UtcNow;
            await _runs.FinishAsync(run, CancellationToken.None);
            await _institutions.UpdateSyncStateAsync(institutionId, SyncStatus.Failed, ex.Message, null, CancellationToken.None);
        }

        return new SyncResult(institutionId, run.Status, run.AccountsSeen, run.TransactionsAdded, run.TransactionsUpdated, run.Error, possibleDuplicates);
    }

    /// <summary>
    /// Lets rows the account had before its handover take over the ids of the same transactions in the new download,
    /// so the upsert that follows updates them instead of inserting copies.
    /// </summary>
    private async Task AdoptHandedOverHistoryAsync(long accountId, AccountHandover handover, IReadOnlyList<ProviderTransaction> incoming, CancellationToken ct)
    {
        if (handover.HistoryCutoffUtc is not { } cutoff || incoming.Count == 0) return;
        var window = AccountMergeService.DedupeWindowDays;
        var first = incoming.Min(t => t.PostedAtUtc).Date;
        if (first > cutoff.Date.AddDays(window)) return; // the download is all newer than the handover
        var stored = await _transactions.ListForHandoverAsync(accountId, first.AddDays(-window), cutoff.Date.AddDays(2 * window + 1), ct);
        var matches = HandoverMatcher.Match(incoming, stored, handover, window);
        foreach (var m in matches)
            await _transactions.AdoptExternalIdAsync(m.TransactionId, m.ExternalId, ct);
        if (matches.Count > 0)
            _log.LogInformation("Matched {Count} downloaded transaction(s) of account {AccountId} to history it had before its handover", matches.Count, accountId);
    }

    private async Task<FetchRequest> BuildFetchRequestAsync(Institution inst, CancellationToken ct)
    {
        // A connection's first sync asks for the full initial history even when it took over accounts that already have
        // some: its other accounts are new, and the handed-over ones are matched rather than duplicated.
        var latest = inst.LastSyncAtUtc is null ? null : await _transactions.GetLatestPostedAsync(inst.Id, ct);
        var start = latest is { } l
            ? l.AddDays(-_settings.IncrementalOverlapDays)
            : DateTime.UtcNow.AddDays(-_settings.InitialHistoryDays);
        return new FetchRequest(StartUtc: start.Date, EndUtc: null, IncludePending: _settings.IncludePending);
    }
}
