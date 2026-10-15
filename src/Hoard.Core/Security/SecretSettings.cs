using Dapper;
using Hoard.Core.Data;

namespace Hoard.Core.Security;

public interface ISecretSettingsStore
{
    Task<SealedSecret?> GetAsync(string key, CancellationToken ct = default);
    Task SaveAsync(string key, SealedSecret secret, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class SecretSettingsStore : ISecretSettingsStore
{
    private readonly IDbConnectionFactory _f;
    public SecretSettingsStore(IDbConnectionFactory f) => _f = f;

    public async Task<SealedSecret?> GetAsync(string key, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SealedSecret>(
            "SELECT key_version AS KeyVersion, nonce AS Nonce, ciphertext AS Ciphertext, tag AS Tag FROM secret_settings WHERE setting_key = @key", new { key });
    }

    public async Task SaveAsync(string key, SealedSecret s, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO secret_settings (setting_key, key_version, nonce, ciphertext, tag, updated_at_utc)
            VALUES (@key, @KeyVersion, @Nonce, @Ciphertext, @Tag, @now)
            ON DUPLICATE KEY UPDATE key_version = VALUES(key_version), nonce = VALUES(nonce),
                                    ciphertext = VALUES(ciphertext), tag = VALUES(tag), updated_at_utc = VALUES(updated_at_utc)
            """, new { key, s.KeyVersion, s.Nonce, s.Ciphertext, s.Tag, now = DateTime.UtcNow });
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM secret_settings WHERE setting_key = @key", new { key });
    }
}

/// <summary>Application secrets (not tied to an institution), sealed by <see cref="CredentialVault"/> with "hoard:setting:{key}" as associated data.</summary>
public sealed class SecretSettings
{
    private readonly ISecretSettingsStore _store;
    private readonly CredentialVault _vault;

    public SecretSettings(ISecretSettingsStore store, CredentialVault vault)
    {
        _store = store;
        _vault = vault;
    }

    private static string Context(string key) => "hoard:setting:" + key;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        await _store.GetAsync(key, ct) is { } sealedSecret ? await _vault.OpenAsync(Context(key), sealedSecret, ct) : null;

    /// <summary>Null or blank deletes the secret.</summary>
    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) { await _store.DeleteAsync(key, ct); return; }
        await _store.SaveAsync(key, await _vault.SealAsync(Context(key), value.Trim(), ct), ct);
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default) => await _store.GetAsync(key, ct) is not null;
}
