using System.Security.Cryptography;
using System.Text;
using Hoard.Core.Data;

namespace Hoard.Core.Security;

/// <summary>An AES-256-GCM sealed blob as stored in institution_credentials.</summary>
public sealed record SealedSecret(int KeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Envelope encryption for provider credentials:
///   plaintext --AES-256-GCM(data key, AAD = institution id)--> ciphertext (stored in MySQL)
///   data key  --DPAPI-->                                        wrapped key (stored in MySQL data_keys)
/// The data key never touches disk unwrapped. Rotation = insert a new data_keys row and re-seal.
/// </summary>
public sealed class CredentialVault
{
    private readonly IDataKeyStore _keyStore;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Version, byte[] Key)? _current;

    public CredentialVault(IDataKeyStore keyStore, ISecretProtector protector)
    {
        _keyStore = keyStore;
        _protector = protector;
    }

    public Task<SealedSecret> SealAsync(long institutionId, string plaintext, CancellationToken ct = default) =>
        SealAsync(InstitutionContext(institutionId), plaintext, ct);

    public Task<string> OpenAsync(long institutionId, SealedSecret sealedSecret, CancellationToken ct = default) =>
        OpenAsync(InstitutionContext(institutionId), sealedSecret, ct);

    /// <summary>
    /// Seals a secret bound to <paramref name="context"/> (the associated data): it only opens under the same context,
    /// so a blob copied to another row or setting fails authentication. Institutions use "hoard:institution:{id}".
    /// </summary>
    public async Task<SealedSecret> SealAsync(string context, string plaintext, CancellationToken ct = default)
    {
        var (version, key) = await GetCurrentKeyAsync(ct);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(plain);
        return new SealedSecret(version, nonce, cipher, tag);
    }

    public async Task<string> OpenAsync(string context, SealedSecret sealedSecret, CancellationToken ct = default)
    {
        var key = await GetKeyAsync(sealedSecret.KeyVersion, ct);
        var plain = new byte[sealedSecret.Ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, plain, Encoding.UTF8.GetBytes(context));
        var text = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return text;
    }

    private static string InstitutionContext(long institutionId) => "hoard:institution:" + institutionId;

    private async Task<(int Version, byte[] Key)> GetCurrentKeyAsync(CancellationToken ct)
    {
        if (_current is { } c) return c;
        await _gate.WaitAsync(ct);
        try
        {
            if (_current is { } c2) return c2;
            var latest = await _keyStore.GetLatestAsync(ct);
            if (latest is null)
            {
                var raw = RandomNumberGenerator.GetBytes(32);
                var wrapped = _protector.Protect(raw);
                var version = await _keyStore.InsertAsync(wrapped, _protector.ScopeName, ct);
                _current = (version, raw);
            }
            else
            {
                _current = (latest.KeyVersion, Unwrap(latest));
            }
            return _current.Value;
        }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> GetKeyAsync(int version, CancellationToken ct)
    {
        if (_current is { } c && c.Version == version) return c.Key;
        var row = await _keyStore.GetAsync(version, ct)
            ?? throw new InvalidOperationException($"Data key version {version} not found.");
        return Unwrap(row);
    }

    private byte[] Unwrap(DataKeyRow row)
    {
        if (!string.Equals(row.ProtectionScope, _protector.ScopeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Data key v{row.KeyVersion} was protected with scope '{row.ProtectionScope}' but this process uses '{_protector.ScopeName}'. " +
                "Run the service under the same Windows account that configured Hoard, or re-enter credentials after changing Security.ProtectionScope.");
        try
        {
            return _protector.Unprotect(row.WrappedKey);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Unable to unwrap the Hoard data key. This usually means a different Windows user (or machine) is running Hoard than the one that configured it.", ex);
        }
    }
}
