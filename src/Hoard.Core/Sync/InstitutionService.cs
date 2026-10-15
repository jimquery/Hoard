using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Providers;
using Hoard.Core.Providers.Plaid;
using Hoard.Core.Providers.SimpleFin;
using Hoard.Core.Security;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>
/// A Plaid Item that has been linked and exchanged but not yet saved: the user is choosing, per Plaid account, whether
/// it is a new account or one Hoard already has. Discard it (<see cref="InstitutionService.DiscardPlaidConnectionAsync"/>)
/// if the user cancels, so the Item does not linger at Plaid.
/// </summary>
/// <param name="Suggestions">Plaid account id → the existing account it most likely is (see <see cref="AccountLinkSuggester"/>).</param>
/// <param name="Candidates">Existing accounts it may be linked to.</param>
public sealed record PendingPlaidConnection(
    PlaidCredential Credential,
    IReadOnlyList<PlaidAccountInfo> Accounts,
    IReadOnlyDictionary<string, long> Suggestions,
    IReadOnlyList<AccountSummary> Candidates);

/// <summary>Adds, removes and re-credentials institutions. Credentials go through <see cref="CredentialVault"/> only.</summary>
public sealed class InstitutionService
{
    /// <summary>Plaid's per-end-user id. Hoard has one user per database, and the value must not be personal data.</summary>
    public const string PlaidClientUserId = "hoard-local-user";
    /// <summary>How often a waiting Hosted Link session is polled.</summary>
    public static readonly TimeSpan PlaidPollInterval = TimeSpan.FromSeconds(3);

    private readonly IInstitutionRepository _institutions;
    private readonly IAccountRepository _accounts;
    private readonly IAccountHandoverRepository _handovers;
    private readonly CredentialVault _vault;
    private readonly SimpleFinClient _simpleFin;
    private readonly PlaidClient _plaid;
    private readonly ILogger<InstitutionService> _log;

    public InstitutionService(IInstitutionRepository institutions, IAccountRepository accounts, IAccountHandoverRepository handovers,
        CredentialVault vault, SimpleFinClient simpleFin, PlaidClient plaid, ILogger<InstitutionService> log)
    {
        _institutions = institutions;
        _accounts = accounts;
        _handovers = handovers;
        _vault = vault;
        _simpleFin = simpleFin;
        _plaid = plaid;
        _log = log;
    }

    /// <summary>
    /// Claims a SimpleFIN setup token (one-time use) and stores the resulting access URL, sealed.
    /// The setup token itself is never persisted.
    /// </summary>
    public async Task<Institution> AddSimpleFinAsync(string displayName, string setupToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A name is required.", nameof(displayName));
        var accessUrl = await _simpleFin.ClaimSetupTokenAsync(setupToken, ct);

        var inst = new Institution
        {
            Name = displayName.Trim(),
            Provider = ProviderKind.SimpleFin,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            LastSyncStatus = SyncStatus.Never,
        };
        inst.Id = await _institutions.InsertAsync(inst, ct);

        var sealedCred = await _vault.SealAsync(inst.Id, new SimpleFinCredential(accessUrl).ToJson(), ct);
        await _institutions.SaveCredentialAsync(inst.Id, sealedCred, ct);
        return inst;
    }

    /// <summary>Replaces the stored credential for an existing institution (e.g. after the token was revoked).</summary>
    public async Task ReplaceSimpleFinTokenAsync(long institutionId, string setupToken, CancellationToken ct = default)
    {
        var accessUrl = await _simpleFin.ClaimSetupTokenAsync(setupToken, ct);
        var sealedCred = await _vault.SealAsync(institutionId, new SimpleFinCredential(accessUrl).ToJson(), ct);
        await _institutions.SaveCredentialAsync(institutionId, sealedCred, ct);
        await _institutions.UpdateSyncStateAsync(institutionId, SyncStatus.Never, null, null, ct);
    }

    /// <summary>The Plaid app keys stored with the most recently added Plaid institution, to pre-fill the next one.</summary>
    public async Task<PlaidAppKeys?> GetSavedPlaidKeysAsync(CancellationToken ct = default)
    {
        foreach (var inst in (await _institutions.ListAsync(ct)).Where(i => i.Provider == ProviderKind.Plaid).OrderByDescending(i => i.CreatedAtUtc))
        {
            try
            {
                return (await OpenPlaidCredentialAsync(inst.Id, ct)).Keys;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not read the Plaid credential of {Institution}", inst.Name);
            }
        }
        return null;
    }

    /// <summary>Step 1 of adding a Plaid institution: a Hosted Link session for the user to complete in their browser.</summary>
    public Task<PlaidLinkSession> StartPlaidLinkAsync(PlaidAppKeys keys, CancellationToken ct = default)
    {
        ValidateKeys(keys);
        return WrapPlaid(() => _plaid.CreateHostedLinkAsync(keys, PlaidClientUserId, null, ct));
    }

    /// <summary>Step 2: polls the session until the user finishes or closes Plaid Link (or <paramref name="ct"/> is cancelled).</summary>
    public async Task<PlaidLinkStatus> WaitForPlaidLinkAsync(PlaidAppKeys keys, string linkToken, bool updateMode = false, CancellationToken ct = default)
    {
        while (true)
        {
            await Task.Delay(PlaidPollInterval, ct);
            var status = await WrapPlaid(() => _plaid.GetLinkStatusAsync(keys, linkToken, updateMode, ct));
            if (status.State != PlaidLinkState.Pending) return status;
        }
    }

    /// <summary>
    /// Step 3: exchanges the public token for the Item's access token and reads its accounts, with a suggested existing
    /// account for each. Nothing is saved in Hoard yet.
    /// </summary>
    public async Task<PendingPlaidConnection> FinishPlaidLinkAsync(PlaidAppKeys keys, PlaidLinkStatus status, CancellationToken ct = default)
    {
        if (status.State != PlaidLinkState.Succeeded || string.IsNullOrEmpty(status.PublicToken))
            throw new InvalidOperationException(status.Message ?? "Plaid Link did not link an institution.");

        var (accessToken, itemId) = await WrapPlaid(() => _plaid.ExchangePublicTokenAsync(keys, status.PublicToken, ct));
        var institutionName = status.InstitutionName;
        if (string.IsNullOrWhiteSpace(institutionName) && !string.IsNullOrEmpty(status.InstitutionId))
        {
            try { institutionName = await _plaid.GetInstitutionNameAsync(keys, status.InstitutionId, ct); }
            catch (PlaidApiException ex) { _log.LogWarning(ex, "Could not look up Plaid institution {InstitutionId}", status.InstitutionId); }
        }
        var credential = new PlaidCredential(keys.Environment, keys.ClientId, keys.Secret, accessToken, itemId, status.InstitutionId, institutionName);

        IReadOnlyList<PlaidAccountInfo> accounts;
        try
        {
            accounts = await WrapPlaid(() => _plaid.GetAccountsAsync(keys, accessToken, ct));
        }
        catch
        {
            await RemovePlaidItemQuietlyAsync(credential);
            throw;
        }

        // Any account can be linked except the properties, which are valued rather than synced.
        var realEstate = (await _institutions.ListAsync(ct)).Where(i => i.Provider == ProviderKind.RealEstate).Select(i => i.Id).ToHashSet();
        var candidates = (await _accounts.ListSummariesAsync(includeHidden: true, ct))
            .Where(a => !realEstate.Contains(a.InstitutionId)).ToList();
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var existing = (await _accounts.ListAllAsync(ct)).Where(a => candidateIds.Contains(a.Id)).ToList();
        var suggestions = AccountLinkSuggester.Suggest(
            accounts.Select(a => new LinkableAccount(a.AccountId, PlaidClient.DisplayName(a), a.Mask, institutionName, a.Currency, a.Balance)).ToList(),
            existing);
        return new PendingPlaidConnection(credential, accounts, suggestions, candidates);
    }

    /// <summary>
    /// Step 4: saves the Plaid institution. Each Plaid account mapped to an existing account in <paramref name="links"/>
    /// takes that account over (<see cref="IAccountHandoverRepository.HandOverAsync"/>), keeping its history; the
    /// others become new accounts. Returns the new institution; its first sync downloads the transactions.
    /// </summary>
    public async Task<Institution> CommitPlaidConnectionAsync(PendingPlaidConnection pending, string displayName,
        IReadOnlyDictionary<string, long?> links, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A name is required.", nameof(displayName));
        var targets = links.Values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (targets.Count != targets.Distinct().Count())
            throw new InvalidOperationException("Each existing account can be linked to only one Plaid account.");

        var inst = new Institution
        {
            Name = displayName.Trim(),
            Provider = ProviderKind.Plaid,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            LastSyncStatus = SyncStatus.Never,
        };
        inst.Id = await _institutions.InsertAsync(inst, ct);
        var sealedCred = await _vault.SealAsync(inst.Id, pending.Credential.ToJson(), ct);
        await _institutions.SaveCredentialAsync(inst.Id, sealedCred, ct);

        var now = DateTime.UtcNow;
        foreach (var a in pending.Accounts)
        {
            var identity = new Account
            {
                InstitutionId = inst.Id,
                ExternalId = a.AccountId,
                Name = PlaidClient.DisplayName(a),
                OrgName = pending.Credential.InstitutionName,
                Currency = a.Currency,
                Balance = a.Balance ?? 0m,
                AvailableBalance = a.AvailableBalance,
                BalanceDateUtc = now,
            };
            if (links.TryGetValue(a.AccountId, out var target) && target is { } accountId)
            {
                await _handovers.HandOverAsync(accountId, identity, ct);
                _log.LogInformation("Account {AccountId} handed over to Plaid institution {Institution} as {PlaidAccount}", accountId, inst.Name, a.AccountId);
            }
            else
            {
                // Created now (rather than at the first sync) so the duplicate check does not flag an account the user
                // just chose to keep separate.
                await _accounts.UpsertAsync(identity, ct);
            }
        }
        return inst;
    }

    /// <summary>The user cancelled after linking: delete the Item at Plaid so it does not count against the plan.</summary>
    public Task DiscardPlaidConnectionAsync(PendingPlaidConnection pending) => RemovePlaidItemQuietlyAsync(pending.Credential);

    /// <summary>
    /// Plaid update mode, for an Item that needs the user to sign in to the bank again (ITEM_LOGIN_REQUIRED): returns a
    /// Hosted Link session for the existing Item and the keys to poll it with.
    /// </summary>
    public async Task<(PlaidLinkSession Session, PlaidAppKeys Keys)> StartPlaidReconnectAsync(long institutionId, CancellationToken ct = default)
    {
        var cred = await OpenPlaidCredentialAsync(institutionId, ct);
        var session = await WrapPlaid(() => _plaid.CreateHostedLinkAsync(cred.Keys, PlaidClientUserId, cred.AccessToken, ct));
        return (session, cred.Keys);
    }

    /// <summary>After a successful reconnect the access token is unchanged; only the sync error is cleared.</summary>
    public Task CompletePlaidReconnectAsync(long institutionId, CancellationToken ct = default) =>
        _institutions.UpdateSyncStateAsync(institutionId, SyncStatus.Never, null, null, ct);

    /// <summary>
    /// Deletes the institution and, via cascading foreign keys, its credential, accounts, transactions and history.
    /// Accounts it took over from another connection that still exists are handed back first, history intact; a Plaid
    /// Item is also removed at Plaid. Returns how many accounts were handed back.
    /// </summary>
    public async Task<int> RemoveAsync(long institutionId, CancellationToken ct = default)
    {
        var inst = await _institutions.GetAsync(institutionId, ct);
        var returned = await _handovers.ReturnAccountsAsync(institutionId, ct);
        if (inst?.Provider == ProviderKind.Plaid)
        {
            try { await RemovePlaidItemQuietlyAsync(await OpenPlaidCredentialAsync(institutionId, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Could not read the Plaid credential of {Institution} to remove its Item", inst.Name); }
        }
        await _institutions.DeleteAsync(institutionId, ct);
        return returned;
    }

    private async Task<PlaidCredential> OpenPlaidCredentialAsync(long institutionId, CancellationToken ct)
    {
        var sealedCred = await _institutions.GetCredentialAsync(institutionId, ct)
            ?? throw new ProviderException("No credentials stored for this institution.", isAuthFailure: true);
        return PlaidCredential.FromJson(await _vault.OpenAsync(institutionId, sealedCred, ct));
    }

    private async Task RemovePlaidItemQuietlyAsync(PlaidCredential cred)
    {
        try
        {
            await _plaid.RemoveItemAsync(cred.Keys, cred.AccessToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not remove Plaid Item {ItemId}; remove it from the Plaid dashboard if it is no longer used", cred.ItemId);
        }
    }

    private static void ValidateKeys(PlaidAppKeys keys)
    {
        if (string.IsNullOrWhiteSpace(keys.ClientId)) throw new ArgumentException("Enter the Plaid client id.");
        if (string.IsNullOrWhiteSpace(keys.Secret)) throw new ArgumentException("Enter the Plaid secret for the chosen environment.");
    }

    /// <summary>Plaid errors surface as <see cref="ProviderException"/>, like every other provider's.</summary>
    private static async Task<T> WrapPlaid<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (PlaidApiException ex) { throw new ProviderException(ex.Message, ex.IsAuthFailure, ex); }
    }
}
