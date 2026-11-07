using Hoard.Core.Duplicates;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class DuplicateFinderTests
{
    private static readonly DateTime Day = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
    private long _id;

    private DuplicateCandidateRow Row(int dayOffset, decimal amount, string description, long account = 1, string? payee = null,
        string? externalId = "x", bool marked = false) => new()
    {
        Id = ++_id, AccountId = account, AccountName = $"Account {account}", PostedAtUtc = Day.AddDays(dayOffset), Amount = amount,
        Description = description, Payee = payee, ExternalId = externalId is null ? null : externalId + _id,
        CreatedAtUtc = Day.AddDays(dayOffset).AddHours(_id), IsDuplicate = marked,
    };

    [Fact]
    public void SameAccountAmountAndVendor_WithinTheWindow_IsAGroup()
    {
        var a = Row(0, -42.10m, "SQ *BLUE BOTTLE COFFEE 0412");
        var b = Row(1, -42.10m, "Blue Bottle Coffee", externalId: null);
        var group = Assert.Single(DuplicateFinder.Find([a, b], windowDays: 2));
        Assert.Equal([a.Id, b.Id], group.Rows.Select(r => r.Id));
        Assert.Equal(a.Id, group.SuggestedKeepId); // the provider-synced row, not the file import
    }

    [Fact]
    public void OutsideTheWindow_DifferentAmountAccountOrVendor_AreNotGroups()
    {
        var rows = new[]
        {
            Row(0, -20m, "KROGER #412"), Row(3, -20m, "KROGER #412"),          // 3 days apart
            Row(10, -20m, "KROGER"), Row(10, -21m, "KROGER"),                   // amount differs
            Row(20, -20m, "KROGER"), Row(20, -20m, "KROGER", account: 2),       // account differs
            Row(30, -20m, "KROGER"), Row(30, -20m, "SHELL OIL 5741"),          // vendor differs
        };
        Assert.Empty(DuplicateFinder.Find(rows, windowDays: 2));
    }

    [Fact]
    public void VendorPrefix_MatchesOnAWordBoundaryOnly()
    {
        Assert.True(DuplicateFinder.SameVendor(["AMAZON"], ["AMAZON MKTPLACE PMTS"]));
        Assert.False(DuplicateFinder.SameVendor(["AMAZON"], ["AMAZONIA GRILL"]));
        Assert.False(DuplicateFinder.SameVendor(["BP"], ["BP CONNECT"])); // too short to prefix-match
    }

    [Fact]
    public void ChecksWithDifferentNumbers_AreNotDuplicates()
    {
        var rows = new[] { Row(0, -120m, "CHECK 190 *2716"), Row(0, -120m, "CHECK 193 *2723"), Row(5, -80m, "CHECK 201"), Row(6, -80m, "Check #0201") };
        var group = Assert.Single(DuplicateFinder.Find(rows, windowDays: 2));
        Assert.Equal(-80m, group.Rows[0].Amount);
    }

    [Fact]
    public void Matches_Chain_IntoOneGroup()
    {
        var rows = new[] { Row(0, -9.99m, "NETFLIX.COM"), Row(2, -9.99m, "NETFLIX.COM"), Row(4, -9.99m, "NETFLIX.COM") };
        Assert.Equal(3, Assert.Single(DuplicateFinder.Find(rows, windowDays: 2)).Rows.Count);
    }

    [Fact]
    public void DismissedPair_DoesNotLink()
    {
        var a = Row(0, -5m, "STARBUCKS 1234");
        var b = Row(0, -5m, "STARBUCKS 1234");
        var dismissed = new HashSet<(long, long)> { DuplicateFinder.Pair(b.Id, a.Id) };
        Assert.Empty(DuplicateFinder.Find([a, b], 2, dismissed));
    }

    [Fact]
    public void ResolvedGroups_AreHiddenUnlessAsked()
    {
        var a = Row(0, -60m, "COSTCO WHSE");
        var b = Row(1, -60m, "COSTCO WHSE", marked: true);
        Assert.Empty(DuplicateFinder.Find([a, b], 2));
        var group = Assert.Single(DuplicateFinder.Find([a, b], 2, includeResolved: true));
        Assert.True(group.IsResolved);
        Assert.Equal(a.Id, group.SuggestedKeepId);
    }
}
