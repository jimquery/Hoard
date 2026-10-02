namespace Hoard.Core.Domain;

/// <summary>Which connectivity mechanism an institution uses. New providers are added here.</summary>
public enum ProviderKind
{
    SimpleFin = 1,
    /// <summary>The synthetic "Real estate" institution: properties valued online (RentCast) or by hand.</summary>
    RealEstate = 2,
    /// <summary>The synthetic "Imported" institution holding history-only accounts created by a CSV import; never synced.</summary>
    Imported = 3,
    // Future: Plaid = 4, OfxDirectConnect = 5, ...
}

public enum SyncStatus
{
    Never = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    PartialSuccess = 4,
}

/// <summary>Broad classification of an account for net-worth reporting.</summary>
public enum AccountKind
{
    Asset = 1,
    Liability = 2,
}

/// <summary>A connection to a financial institution (e.g. one SimpleFIN access token).</summary>
public sealed class Institution
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public ProviderKind Provider { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public SyncStatus LastSyncStatus { get; set; } = SyncStatus.Never;
    public string? LastError { get; set; }
}

public sealed class AccountCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public AccountKind Kind { get; set; } = AccountKind.Asset;
    public int SortOrder { get; set; }
    /// <summary>Chart color as #RRGGBB, or null for an automatic palette color.</summary>
    public string? Color { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>Whether transactions in accounts of this type count in the spending and income-vs-expense reports.</summary>
    public bool IncludeInSpending { get; set; }
}

public sealed class Account
{
    public long Id { get; set; }
    public long InstitutionId { get; set; }
    /// <summary>Provider-assigned stable identifier for the account.</summary>
    public string ExternalId { get; set; } = "";
    /// <summary>Name as reported by the bank/provider; refreshed on every sync.</summary>
    public string Name { get; set; } = "";
    /// <summary>User override of <see cref="Name"/>; never touched by sync.</summary>
    public string? DisplayName { get; set; }
    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string? OrgName { get; set; }
    public string? OrgDomain { get; set; }
    public string Currency { get; set; } = "USD";
    public long? CategoryId { get; set; }
    public long? OwnerId { get; set; }
    public decimal Balance { get; set; }
    public decimal? AvailableBalance { get; set; }
    public DateTime? BalanceDateUtc { get; set; }
    public bool IsHidden { get; set; }
    /// <summary>Shown in lists but left out of net-worth totals, the dashboard totals and net-worth reports.</summary>
    public bool ExcludeFromNetWorth { get; set; }
    /// <summary>
    /// Set when a sync inserted this account and it looks like a second copy of an existing one (a provider reconnect
    /// that issued new ids). Cleared by merging or dismissing the pair.
    /// </summary>
    public long? PossibleDuplicateOf { get; set; }
    /// <summary>The user said this account is not a duplicate; scans leave it alone from then on.</summary>
    public bool DuplicateDismissed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class Transaction
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    /// <summary>Provider-assigned transaction id. Null when imported from a source that has none (e.g. CSV).</summary>
    public string? ExternalId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public DateTime? TransactedAtUtc { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? Payee { get; set; }
    public string? Memo { get; set; }
    public bool IsPending { get; set; }
    /// <summary>SHA-256 over (account, posted date, amount, normalized description); the fallback dedupe key.</summary>
    public string Fingerprint { get; set; } = "";
    public long? CategoryId { get; set; }
    public CategorySource CategorySource { get; set; } = CategorySource.None;
    public long? CategoryRuleId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>How a transaction got its category. Manual assignments are never overwritten by rules.</summary>
public enum CategorySource
{
    None = 0,
    Rule = 1,
    Manual = 2,
    /// <summary>
    /// Came with a CSV import (e.g. Empower's category) because no rule claimed the row at import time.
    /// Later rule sweeps leave it alone, like a manual choice; a rule that does match during an import
    /// wins over the file and the row is stored as <see cref="Rule"/> instead.
    /// </summary>
    Imported = 3,
}

public enum CategoryKind
{
    Expense = 1,
    Income = 2,
    Transfer = 3,
}

/// <summary>Two-level tree: parent groups (ParentId == null) and their sub-categories.</summary>
public sealed class TransactionCategory
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public CategoryKind Kind { get; set; } = CategoryKind.Expense;
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }
    public bool IsHidden { get; set; }
}

public enum RuleMatchField
{
    Description = 1,
    Payee = 2,
    Any = 3,
}

public enum RuleMatchType
{
    Contains = 1,
    StartsWith = 2,
    Exact = 3,
    Regex = 4,
}

/// <summary>An auto-categorization rule. Rules are evaluated by ascending Priority then Id; the first match wins.</summary>
public sealed class CategoryRule
{
    public long Id { get; set; }
    public int Priority { get; set; } = 100;
    public RuleMatchField MatchField { get; set; } = RuleMatchField.Any;
    public RuleMatchType MatchType { get; set; } = RuleMatchType.Contains;
    public string Pattern { get; set; } = "";
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    /// <summary>Restrict the rule to one account, or null for all accounts.</summary>
    public long? AccountId { get; set; }
    public long CategoryId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsSystem { get; set; }
    public int HitCount { get; set; }
    public DateTime? LastHitAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A person in the household; accounts can be assigned an owner for per-person reporting.</summary>
public sealed class HouseholdMember
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Date only (time component ignored). Null when unknown.</summary>
    public DateTime? DateOfBirth { get; set; }
    public string? Relationship { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public int? AgeOn(DateOnly date) => DateOfBirth is { } dob ? AgeCalculator.AgeOn(DateOnly.FromDateTime(dob), date) : null;
}

public static class AgeCalculator
{
    public static int AgeOn(DateOnly dateOfBirth, DateOnly date)
    {
        var age = date.Year - dateOfBirth.Year;
        if (date < dateOfBirth.AddYears(age)) age--;
        return Math.Max(0, age);
    }

    /// <summary>The date the person turns <paramref name="years"/> (Feb 29 birthdays roll to Mar 1 in non-leap years).</summary>
    public static DateOnly BirthdayAt(DateOnly dateOfBirth, int years) => dateOfBirth.AddYears(years);
}

public enum ValuationSource
{
    OnlineAvm = 1,
    Manual = 2,
}

/// <summary>A real-estate holding tracked by address. Mirrored as an account so it counts toward net worth.</summary>
public sealed class Property
{
    public long Id { get; set; }
    public long? AccountId { get; set; }
    public string Nickname { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Zip { get; set; } = "";
    public string? PropertyType { get; set; }
    public decimal? Bedrooms { get; set; }
    public decimal? Bathrooms { get; set; }
    public int? SquareFootage { get; set; }
    public decimal? PurchasePrice { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public ValuationSource ValuationSource { get; set; } = ValuationSource.OnlineAvm;
    public decimal? ManualValue { get; set; }
    public int RefreshDays { get; set; } = 30;
    public decimal? LastValue { get; set; }
    public decimal? LastValueLow { get; set; }
    public decimal? LastValueHigh { get; set; }
    public DateTime? LastValuedAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>"Street, City, State, Zip" — the form the valuation service expects.</summary>
    public string FormattedAddress =>
        string.Join(", ", new[] { string.IsNullOrWhiteSpace(AddressLine2) ? AddressLine1 : $"{AddressLine1} {AddressLine2}", City, $"{State} {Zip}".Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// The value that counts toward net worth. Manual: the entered value, else the purchase price (an old online estimate is
    /// never used once the owner has opted out of it). Online: the latest estimate, else the purchase price until one arrives.
    /// </summary>
    public decimal CurrentValue => ValuationSource == ValuationSource.Manual
        ? ManualValue ?? PurchasePrice ?? LastValue ?? 0m
        : LastValue ?? ManualValue ?? PurchasePrice ?? 0m;
}

public sealed class PropertyValuation
{
    public long Id { get; set; }
    public long PropertyId { get; set; }
    public DateTime AsOf { get; set; }
    public decimal Value { get; set; }
    public decimal? ValueLow { get; set; }
    public decimal? ValueHigh { get; set; }
    public ValuationSource Source { get; set; }
}

/// <summary>Daily balance observation, used for net-worth-over-time reporting.</summary>
public sealed class BalanceSnapshot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public DateOnly AsOf { get; set; }
    public decimal Balance { get; set; }
    public decimal? AvailableBalance { get; set; }
}

public sealed class SyncRun
{
    public long Id { get; set; }
    public long InstitutionId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public SyncStatus Status { get; set; }
    public int AccountsSeen { get; set; }
    public int TransactionsAdded { get; set; }
    public int TransactionsUpdated { get; set; }
    public string? Error { get; set; }
}
