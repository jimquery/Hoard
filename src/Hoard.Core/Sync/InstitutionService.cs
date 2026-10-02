using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Providers.SimpleFin;
using Hoard.Core.Security;

namespace Hoard.Core.Sync;

/// <summary>Adds, removes and re-credentials institutions. Credentials go through <see cref="CredentialVault"/> only.</summary>
public sealed class InstitutionService
{
    private readonly IInstitutionRepository _institutions;
    private readonly CredentialVault _vault;
    private readonly SimpleFinClient _simpleFin;

    public InstitutionService(IInstitutionRepository institutions, CredentialVault vault, SimpleFinClient simpleFin)
    {
        _institutions = institutions;
        _vault = vault;
        _simpleFin = simpleFin;
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

    /// <summary>Deletes the institution and, via cascading foreign keys, its credential, accounts, transactions and history.</summary>
    public Task RemoveAsync(long institutionId, CancellationToken ct = default) => _institutions.DeleteAsync(institutionId, ct);
}
