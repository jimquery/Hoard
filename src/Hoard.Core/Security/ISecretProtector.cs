using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Hoard.Core.Security;

/// <summary>
/// Wraps/unwraps small secrets (data keys, the DB password) using an OS-level protection facility.
/// The production implementation is Windows DPAPI; tests use <see cref="InMemorySecretProtector"/>.
/// </summary>
public interface ISecretProtector
{
    /// <summary>A short identifier stored alongside wrapped keys so a mismatch can be diagnosed.</summary>
    string ScopeName { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows Data Protection API. Keys are bound to the current user (default) or the machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Extra entropy makes Hoard blobs unusable by other DPAPI callers running as the same user.
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("Hoard.v1.DPAPI.Entropy");

    private readonly DataProtectionScope _scope;

    public DpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
    }

    public static DpapiSecretProtector FromScopeName(string? scopeName) =>
        new(string.Equals(scopeName, "LocalMachine", StringComparison.OrdinalIgnoreCase)
            ? DataProtectionScope.LocalMachine
            : DataProtectionScope.CurrentUser);

    public string ScopeName => _scope == DataProtectionScope.LocalMachine ? "LocalMachine" : "CurrentUser";

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, _scope);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, _scope);
}

/// <summary>
/// Non-Windows / test protector. Uses a process-local random key, so nothing it protects survives the process.
/// Never use in production.
/// </summary>
public sealed class InMemorySecretProtector : ISecretProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string ScopeName => "InMemory";

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = protectedData.AsSpan(0, nonceLen);
        var tag = protectedData.AsSpan(nonceLen, tagLen);
        var cipher = protectedData.AsSpan(nonceLen + tagLen);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
