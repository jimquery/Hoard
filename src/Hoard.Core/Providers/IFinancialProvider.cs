using Hoard.Core.Domain;

namespace Hoard.Core.Providers;

/// <summary>Provider-neutral shape of what a sync returns. Providers map their own formats into this.</summary>
public sealed record ProviderTransaction(
    string? ExternalId,
    DateTime PostedAtUtc,
    DateTime? TransactedAtUtc,
    decimal Amount,
    string Description,
    string? Payee,
    string? Memo,
    bool IsPending);

public sealed record ProviderAccount(
    string ExternalId,
    string Name,
    string? OrgName,
    string? OrgDomain,
    string Currency,
    decimal Balance,
    decimal? AvailableBalance,
    DateTime? BalanceDateUtc,
    IReadOnlyList<ProviderTransaction> Transactions);

public sealed record ProviderAccountSet(
    IReadOnlyList<ProviderAccount> Accounts,
    IReadOnlyList<string> Errors);

public sealed record FetchRequest(
    DateTime? StartUtc,
    DateTime? EndUtc,
    bool IncludePending);

/// <summary>
/// A source of account and transaction data. Each institution row references one provider by
/// <see cref="ProviderKind"/>; its credential JSON is opaque to everything except that provider.
/// </summary>
public interface IFinancialProvider
{
    ProviderKind Kind { get; }
    Task<ProviderAccountSet> FetchAsync(string credentialJson, FetchRequest request, CancellationToken ct = default);
}

public sealed class ProviderException : Exception
{
    public bool IsAuthFailure { get; }
    public ProviderException(string message, bool isAuthFailure = false, Exception? inner = null) : base(message, inner)
        => IsAuthFailure = isAuthFailure;
}
