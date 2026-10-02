using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hoard.Core.Sync;

/// <summary>
/// Content-based identity for a transaction, used as the dedupe key when a provider supplies no stable id
/// (CSV/QFX imports, some OFX servers) and as a secondary signal when it does.
/// Two transactions with the same account, posted date, amount and normalized description are considered
/// the same. This deliberately ignores time-of-day and provider-specific memo noise.
/// </summary>
public static partial class TransactionFingerprint
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}\p{N} ]")]
    private static partial Regex NonAlphanumeric();

    public static string Compute(long accountId, DateTime postedAtUtc, decimal amount, string description)
    {
        var canonical = string.Join("|",
            accountId.ToString(CultureInfo.InvariantCulture),
            postedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            amount.ToString("0.00##", CultureInfo.InvariantCulture),
            NormalizeDescription(description));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var s = description.ToUpperInvariant();
        s = NonAlphanumeric().Replace(s, " ");
        s = Whitespace().Replace(s, " ").Trim();
        return s;
    }
}
