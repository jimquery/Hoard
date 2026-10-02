using Hoard.Core.Reporting;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class SubscriptionDetectorTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 26);
    private static long _id;

    private static SpendingTransactionRow Tx(string desc, decimal amount, DateOnly on, string account = "Visa", string? category = "Entertainment") =>
        new() { Id = ++_id, AccountId = 1, AccountName = account, InstitutionName = "Bank", PostedAtUtc = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Amount = -amount, Description = desc, Payee = null, CategoryName = category };

    private static IEnumerable<SpendingTransactionRow> Monthly(string desc, decimal amount, DateOnly first, int count, int jitterDays = 0)
    {
        for (var i = 0; i < count; i++) yield return Tx(desc, amount, first.AddMonths(i).AddDays(i % 2 == 0 ? jitterDays : -jitterDays));
    }

    [Fact]
    public void FindsMonthlyQuarterlyAndYearly_AndComputesAnnualCost()
    {
        var rows = new List<SpendingTransactionRow>();
        rows.AddRange(Monthly("NETFLIX.COM 866-579-7172 CA", 15.49m, new DateOnly(2025, 10, 3), 12, jitterDays: 2));
        for (var q = 0; q < 4; q++) rows.Add(Tx("ADOBE *CREATIVE CLOUD", 59.99m, new DateOnly(2025, 10, 15).AddMonths(3 * q)));
        rows.Add(Tx("AMAZON PRIME MEMBERSHIP", 139.00m, new DateOnly(2024, 8, 20)));
        rows.Add(Tx("AMAZON PRIME MEMBERSHIP", 139.00m, new DateOnly(2025, 8, 21)));
        rows.Add(Tx("AMAZON PRIME MEMBERSHIP", 139.00m, new DateOnly(2026, 8, 19)));
        // Noise: groceries at varying amounts on irregular days are never a subscription.
        rows.Add(Tx("GIANT EAGLE #12", 84.12m, new DateOnly(2026, 8, 2)));
        rows.Add(Tx("GIANT EAGLE #12", 91.30m, new DateOnly(2026, 8, 9)));
        rows.Add(Tx("GIANT EAGLE #12", 62.05m, new DateOnly(2026, 8, 23)));
        rows.Add(Tx("GIANT EAGLE #12", 88.00m, new DateOnly(2026, 9, 1)));

        var subs = SubscriptionDetector.Detect(rows, AsOf);

        Assert.Equal(3, subs.Count);
        var netflix = subs.Single(s => s.VendorKey.Contains("NETFLIX"));
        Assert.Equal(SubscriptionCadence.Monthly, netflix.Cadence);
        Assert.Equal(12, netflix.Occurrences);
        Assert.Equal(15.49m * 12, netflix.AnnualCost);
        Assert.True(netflix.IsActive);

        var adobe = subs.Single(s => s.VendorKey.Contains("ADOBE"));
        Assert.Equal(SubscriptionCadence.Quarterly, adobe.Cadence);
        Assert.Equal(59.99m * 4, adobe.AnnualCost);

        var prime = subs.Single(s => s.VendorKey.Contains("AMAZON"));
        Assert.Equal(SubscriptionCadence.Yearly, prime.Cadence);
        Assert.Equal(139.00m, prime.AnnualCost);
        Assert.Equal(3, prime.Occurrences);
    }

    [Fact]
    public void PriceRise_StaysOneSubscription_AtTheNewPrice()
    {
        var rows = new List<SpendingTransactionRow>();
        rows.AddRange(Monthly("SPOTIFY USA", 10.99m, new DateOnly(2025, 6, 5), 8));   // Jun 2025 – Jan 2026
        rows.AddRange(Monthly("SPOTIFY USA", 12.99m, new DateOnly(2026, 2, 5), 8));   // Feb – Sep 2026: an 18% rise, beyond the cluster tolerance

        var sub = Assert.Single(SubscriptionDetector.Detect(rows, AsOf));
        Assert.Equal(16, sub.Occurrences);
        Assert.Equal(12.99m, sub.LatestAmount);
        Assert.Equal(12.99m * 12, sub.AnnualCost);
        Assert.Equal(new DateOnly(2025, 6, 5), sub.FirstCharge);
    }

    [Fact]
    public void SkippedMonth_IsTolerated_ButIrregularDatesAreNot()
    {
        var rows = new List<SpendingTransactionRow>();
        var first = new DateOnly(2026, 1, 10);
        foreach (var m in new[] { 0, 1, 2, 4, 5, 6, 7 }) rows.Add(Tx("HULU", 17.99m, first.AddMonths(m)));   // March missing
        var sub = Assert.Single(SubscriptionDetector.Detect(rows, AsOf));
        Assert.Equal(SubscriptionCadence.Monthly, sub.Cadence);
        Assert.Equal(7, sub.Occurrences);

        // Same amount, but on random days: not a rhythm.
        var random = new[] { 3, 11, 17, 42, 45, 90, 101, 110 }.Select(d => Tx("PARKING GARAGE", 12.00m, first.AddDays(d))).ToList();
        Assert.Empty(SubscriptionDetector.Detect(random, AsOf));
    }

    [Fact]
    public void LapsedSubscription_IsReportedInactive()
    {
        var rows = Monthly("GYM MEMBERSHIP", 45m, new DateOnly(2025, 6, 1), 6).ToList(); // last charge Nov 2025
        var sub = Assert.Single(SubscriptionDetector.Detect(rows, AsOf));
        Assert.False(sub.IsActive);
        Assert.Equal(new DateOnly(2025, 11, 1), sub.LastCharge);
    }

    [Fact]
    public void TooFewCharges_IsNotASubscription()
    {
        var rows = Monthly("NEW STREAMING", 9.99m, new DateOnly(2026, 8, 1), 2).ToList();
        Assert.Empty(SubscriptionDetector.Detect(rows, AsOf));
    }

    [Fact]
    public void DisplayName_TitleCasesTheKey()
    {
        Assert.Equal("Netflix Com", SubscriptionDetector.DisplayName("NETFLIX COM"));
    }
}
