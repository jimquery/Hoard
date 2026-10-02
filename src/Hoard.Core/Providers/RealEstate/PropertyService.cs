using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Security;
using Hoard.Core.Sync;

namespace Hoard.Core.Providers.RealEstate;

/// <summary>
/// Manages properties and the synthetic "Real estate" institution that hosts them as accounts.
/// The RentCast API key is stored sealed in the institution's credential like any other secret.
/// </summary>
public sealed class PropertyService
{
    public const string InstitutionName = "Real estate";
    public const string DefaultAccountTypeName = "Real Estate";

    private readonly IPropertyRepository _properties;
    private readonly IInstitutionRepository _institutions;
    private readonly IAccountRepository _accounts;
    private readonly CredentialVault _vault;
    private readonly SyncService _sync;
    private readonly RealEstateProvider _provider;

    public PropertyService(IPropertyRepository properties, IInstitutionRepository institutions, IAccountRepository accounts,
        CredentialVault vault, SyncService sync, RealEstateProvider provider)
    {
        _properties = properties;
        _institutions = institutions;
        _accounts = accounts;
        _vault = vault;
        _sync = sync;
        _provider = provider;
    }

    public Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct = default) => _properties.ListAsync(ct);
    public Task<IReadOnlyList<PropertyValuation>> ListValuationsAsync(long propertyId, CancellationToken ct = default) => _properties.ListValuationsAsync(propertyId, ct);

    /// <summary>Finds or creates the Real estate institution (with an empty credential until an API key is entered).</summary>
    public async Task<Institution> EnsureInstitutionAsync(CancellationToken ct = default)
    {
        var existing = (await _institutions.ListAsync(ct)).FirstOrDefault(i => i.Provider == ProviderKind.RealEstate);
        if (existing is not null) return existing;

        var inst = new Institution { Name = InstitutionName, Provider = ProviderKind.RealEstate, IsEnabled = true, CreatedAtUtc = DateTime.UtcNow };
        inst.Id = await _institutions.InsertAsync(inst, ct);
        await SaveCredentialAsync(inst.Id, new RealEstateCredential(ApiKey: null), ct);
        return inst;
    }

    public async Task<bool> HasApiKeyAsync(CancellationToken ct = default)
    {
        var inst = (await _institutions.ListAsync(ct)).FirstOrDefault(i => i.Provider == ProviderKind.RealEstate);
        if (inst is null) return false;
        var sealedCred = await _institutions.GetCredentialAsync(inst.Id, ct);
        if (sealedCred is null) return false;
        var cred = RealEstateCredential.FromJson(await _vault.OpenAsync(inst.Id, sealedCred, ct));
        return !string.IsNullOrWhiteSpace(cred.ApiKey);
    }

    public async Task SetApiKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        var inst = await EnsureInstitutionAsync(ct);
        await SaveCredentialAsync(inst.Id, new RealEstateCredential(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim()), ct);
    }

    private async Task SaveCredentialAsync(long institutionId, RealEstateCredential cred, CancellationToken ct)
    {
        var sealedCred = await _vault.SealAsync(institutionId, cred.ToJson(), ct);
        await _institutions.SaveCredentialAsync(institutionId, sealedCred, ct);
    }

    /// <summary>Adds a property and its mirror account (typed "Real Estate" when that type exists) at its current value.</summary>
    public async Task<Property> AddAsync(Property property, CancellationToken ct = default)
    {
        Validate(property);
        var inst = await EnsureInstitutionAsync(ct);
        property.Id = await _properties.InsertAsync(property, ct);

        var accountId = await MirrorAsync(property, inst.Id, ct);
        var realEstateType = (await _accounts.ListCategoriesAsync(ct))
            .FirstOrDefault(c => string.Equals(c.Name, DefaultAccountTypeName, StringComparison.OrdinalIgnoreCase));
        if (realEstateType is not null) await _accounts.SetCategoryAsync(accountId, realEstateType.Id, ct);

        property.AccountId = accountId;
        await _properties.UpdateAsync(property, ct);
        return property;
    }

    /// <summary>
    /// Saves edits and immediately pushes the property's current value to its account, so switching to a manual value
    /// (or changing it) shows up everywhere without waiting for the next sync.
    /// </summary>
    public async Task UpdateAsync(Property property, CancellationToken ct = default)
    {
        Validate(property);
        var inst = await EnsureInstitutionAsync(ct);
        await _properties.UpdateAsync(property, ct);
        var latest = property.ValuationSource == ValuationSource.OnlineAvm ? (await _properties.ListValuationsAsync(property.Id, ct)).LastOrDefault() : null;
        if (latest is not null && latest.Source == ValuationSource.Manual)
        {
            // Back to online: drop the manual figure that was recorded as the latest value so the next refresh re-queries.
            await _properties.ResetLatestValueAsync(property.Id, ct);
            property.LastValue = null; property.LastValueLow = null; property.LastValueHigh = null; property.LastValuedAtUtc = null; property.LastError = null;
        }
        var accountId = await MirrorAsync(property, inst.Id, ct);
        if (property.AccountId != accountId)
        {
            property.AccountId = accountId;
            await _properties.UpdateAsync(property, ct);
        }
        await _accounts.SetDisplayNameAsync(accountId, null, ct); // the nickname is the account's name
    }

    /// <summary>
    /// Writes the property's current value to its mirror account (creating the account if needed), records today's
    /// balance snapshot and, for manually valued properties, today's valuation. Returns the account id.
    /// </summary>
    private async Task<long> MirrorAsync(Property p, long institutionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (p.ValuationSource == ValuationSource.Manual && p.ManualValue is { } manual)
        {
            await _properties.RecordValuationAsync(p.Id, manual, null, null, ValuationSource.Manual, now, null, ct);
            p.LastValue = manual; p.LastValueLow = null; p.LastValueHigh = null; p.LastValuedAtUtc = now; p.LastError = null;
        }
        var value = p.CurrentValue;
        var accountId = await _accounts.UpsertAsync(new Account
        {
            InstitutionId = institutionId,
            ExternalId = RealEstateProvider.ExternalIdFor(p.Id),
            Name = p.Nickname,
            OrgName = p.ValuationSource == ValuationSource.OnlineAvm ? "RentCast" : "Manual value",
            Currency = "USD",
            Balance = value,
            BalanceDateUtc = now,
        }, ct);
        await _accounts.UpsertSnapshotAsync(new BalanceSnapshot { AccountId = accountId, AsOf = DateOnly.FromDateTime(now), Balance = value }, ct);
        return accountId;
    }

    /// <summary>Removes the property and its mirror account (with its balance history).</summary>
    public async Task RemoveAsync(long propertyId, CancellationToken ct = default)
    {
        var p = await _properties.GetAsync(propertyId, ct);
        if (p is null) return;
        await _properties.DeleteAsync(propertyId, ct);
        if (p.AccountId is { } accountId) await _accounts.DeleteAsync(accountId, ct);
    }

    /// <summary>Runs the Real estate sync; with <paramref name="force"/> every online-valued property is re-estimated now.</summary>
    public async Task<SyncResult> RefreshAsync(bool force, CancellationToken ct = default)
    {
        var inst = await EnsureInstitutionAsync(ct);
        _provider.ForceRefresh = force;
        var result = await _sync.SyncInstitutionAsync(inst.Id, ct);
        await RelinkAccountsAsync(inst.Id, ct);
        return result;
    }

    /// <summary>If the Real estate institution was ever removed and re-created, re-attach properties to their mirror accounts.</summary>
    private async Task RelinkAccountsAsync(long institutionId, CancellationToken ct)
    {
        var orphans = (await _properties.ListAsync(ct)).Where(p => p.AccountId is null).ToList();
        if (orphans.Count == 0) return;
        var accounts = await _accounts.ListByInstitutionAsync(institutionId, ct);
        foreach (var p in orphans)
        {
            var match = accounts.FirstOrDefault(a => a.ExternalId == RealEstateProvider.ExternalIdFor(p.Id));
            if (match is null) continue;
            p.AccountId = match.Id;
            await _properties.UpdateAsync(p, ct);
        }
    }

    private static void Validate(Property p)
    {
        if (string.IsNullOrWhiteSpace(p.AddressLine1) || string.IsNullOrWhiteSpace(p.City) || string.IsNullOrWhiteSpace(p.State) || string.IsNullOrWhiteSpace(p.Zip))
            throw new ArgumentException("Street, city, state and ZIP are required.");
        if (string.IsNullOrWhiteSpace(p.Nickname)) p.Nickname = p.AddressLine1.Trim();
        p.Nickname = p.Nickname.Trim();
        p.AddressLine1 = p.AddressLine1.Trim();
        p.City = p.City.Trim();
        p.State = p.State.Trim().ToUpperInvariant();
        p.Zip = p.Zip.Trim();
        p.RefreshDays = Math.Clamp(p.RefreshDays, 1, 365);
        if (p.ValuationSource == ValuationSource.Manual && p.ManualValue is null && p.PurchasePrice is null)
            throw new ArgumentException("Enter a value (or a purchase price) for a manually valued property.");
    }
}
