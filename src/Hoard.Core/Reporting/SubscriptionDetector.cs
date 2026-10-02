using System.Globalization;
using Hoard.Core.Categorization;

namespace Hoard.Core.Reporting;

/// <summary>Knobs for <see cref="SubscriptionDetector"/>. The defaults suit bank feeds; tests tighten them.</summary>
public sealed class SubscriptionDetectorOptions
{
    /// <summary>Charges within this fraction of each other count as "the same amount" (a small price rise stays in one run).</summary>
    public decimal AmountTolerancePercent { get; init; } = 12m;
    /// <summary>...or within this many currency units, whichever is larger (so $1.99 vs $2.49 still matches).</summary>
    public decimal AmountToleranceAbsolute { get; init; } = 1.00m;
    /// <summary>Fewest charges that make a run a subscription, per cadence.</summary>
    public int MinMonthly { get; init; } = 3;
    public int MinQuarterly { get; init; } = 3;
    public int MinYearly { get; init; } = 2;
    /// <summary>Fraction of the gaps between charges that must land on the cadence (or a whole multiple of it, a skipped bill).</summary>
    public double MinRegularity { get; init; } = 0.75;
    /// <summary>A subscription is active while its last charge is younger than this many periods.</summary>
    public double ActiveWithinPeriods { get; init; } = 1.75;
}

/// <summary>
/// Pure recurring-charge finder. Expense transactions are grouped by merchant (<see cref="MerchantKey"/>, so
/// "NETFLIX.COM 866-579-7172" and "NETFLIX.COM" are one vendor), each vendor's charges are clustered by amount, and
/// each cluster's posting dates are tested for a monthly, quarterly or yearly rhythm. Clusters of one vendor on the
/// same cadence that follow each other in time are merged, so a price change stays one subscription. Nothing is
/// stored: the report is recomputed from transactions every time.
/// </summary>
public static class SubscriptionDetector
{
    private static readonly (SubscriptionCadence Cadence, double PeriodDays, double ToleranceDays, int PerYear)[] Cadences =
    [
        (SubscriptionCadence.Monthly,   30.44,  6.0, 12),
        (SubscriptionCadence.Quarterly, 91.31, 12.0, 4),
        (SubscriptionCadence.Yearly,   365.25, 20.0, 1),
    ];

    public static IReadOnlyList<Subscription> Detect(IReadOnlyList<SpendingTransactionRow> rows, DateOnly asOf, SubscriptionDetectorOptions? options = null)
    {
        options ??= new SubscriptionDetectorOptions();
        var found = new List<Subscription>();

        var byVendor = rows.Where(r => r.Amount < 0)
            .GroupBy(r => MerchantKey.Extract(r.Description, r.Payee))
            .Where(g => g.Key.Length > 0);

        foreach (var vendor in byVendor)
        {
            var runs = new List<Subscription>();
            foreach (var cluster in ClusterByAmount(vendor.ToList(), options))
            {
                if (Classify(cluster, vendor.Key, asOf, options) is { } sub) runs.Add(sub);
            }
            found.AddRange(MergeAdjacentRuns(runs, asOf, options));
        }

        return found.OrderByDescending(s => s.IsActive).ThenByDescending(s => s.AnnualCost).ThenBy(s => s.Vendor).ToList();
    }

    /// <summary>Greedy clustering on the sorted magnitudes: a charge joins the current cluster while it is within tolerance of the cluster's median.</summary>
    private static IEnumerable<List<SpendingTransactionRow>> ClusterByAmount(List<SpendingTransactionRow> rows, SubscriptionDetectorOptions o)
    {
        var sorted = rows.OrderBy(r => -r.Amount).ToList();
        var current = new List<SpendingTransactionRow>();
        foreach (var r in sorted)
        {
            if (current.Count > 0 && !Similar(Median(current.Select(c => -c.Amount)), -r.Amount, o))
            {
                yield return current;
                current = new List<SpendingTransactionRow>();
            }
            current.Add(r);
        }
        if (current.Count > 0) yield return current;
    }

    private static bool Similar(decimal a, decimal b, SubscriptionDetectorOptions o)
    {
        var tolerance = Math.Max(o.AmountToleranceAbsolute, Math.Max(a, b) * o.AmountTolerancePercent / 100m);
        return Math.Abs(a - b) <= tolerance;
    }

    private static Subscription? Classify(List<SpendingTransactionRow> cluster, string vendorKey, DateOnly asOf, SubscriptionDetectorOptions o)
    {
        // One charge per day per vendor/amount: the same bill posted twice is a bank quirk, not a rhythm.
        var charges = cluster.GroupBy(r => DateOnly.FromDateTime(r.PostedAtUtc)).Select(g => g.First())
                             .OrderBy(r => r.PostedAtUtc).ToList();
        if (charges.Count < 2) return null;

        var gaps = new List<double>();
        for (var i = 1; i < charges.Count; i++)
            gaps.Add((charges[i].PostedAtUtc.Date - charges[i - 1].PostedAtUtc.Date).TotalDays);
        var medianGap = Median(gaps);

        // The cadence whose period the typical gap lands on.
        (SubscriptionCadence Cadence, double PeriodDays, double ToleranceDays, int PerYear)? match = null;
        foreach (var c in Cadences)
            if (Math.Abs(medianGap - c.PeriodDays) <= c.ToleranceDays) { match = c; break; }
        if (match is null) return null;
        var (cadence, period, tolerance, perYear) = match.Value;

        var minCount = cadence switch { SubscriptionCadence.Monthly => o.MinMonthly, SubscriptionCadence.Quarterly => o.MinQuarterly, _ => o.MinYearly };
        if (charges.Count < minCount) return null;

        // Every gap should be the period or a whole multiple of it (a skipped or double-billed cycle).
        var regular = gaps.Count(g => IsMultiple(g, period, tolerance));
        if ((double)regular / gaps.Count < o.MinRegularity) return null;

        return Build(vendorKey, cadence, perYear, charges, asOf, period, o);
    }

    private static bool IsMultiple(double gap, double period, double tolerance)
    {
        var k = Math.Round(gap / period);
        return k >= 1 && Math.Abs(gap - k * period) <= tolerance * Math.Max(1, k);
    }

    private static Subscription Build(string vendorKey, SubscriptionCadence cadence, int perYear, List<SpendingTransactionRow> charges, DateOnly asOf, double period, SubscriptionDetectorOptions o)
    {
        var last = charges[^1];
        var lastOn = DateOnly.FromDateTime(last.PostedAtUtc);
        var latest = -last.Amount;
        var typical = Median(charges.Select(c => -c.Amount));
        var active = (asOf.ToDateTime(TimeOnly.MinValue) - last.PostedAtUtc.Date).TotalDays <= period * o.ActiveWithinPeriods;
        return new Subscription
        {
            VendorKey = vendorKey,
            Vendor = DisplayName(vendorKey),
            Cadence = cadence,
            TypicalAmount = typical,
            LatestAmount = latest,
            AnnualCost = Math.Round(latest * perYear, 2),
            FirstCharge = DateOnly.FromDateTime(charges[0].PostedAtUtc),
            LastCharge = lastOn,
            IsActive = active,
            Accounts = string.Join(", ", charges.Select(c => c.AccountName).Distinct(StringComparer.OrdinalIgnoreCase)),
            CategoryName = charges.GroupBy(c => c.CategoryName).OrderByDescending(g => g.Count()).First().Key,
            Charges = charges.Select(c => new SubscriptionCharge
            {
                TransactionId = c.Id,
                PostedOn = DateOnly.FromDateTime(c.PostedAtUtc),
                Amount = -c.Amount,
                Description = c.Description,
                AccountName = c.AccountName,
                CategoryName = c.CategoryName,
            }).ToList(),
        };
    }

    /// <summary>
    /// Two runs of the same vendor on the same cadence, where one starts about a period after the other ends, are
    /// the same subscription across a price change. The merged run keeps every charge and the later price.
    /// </summary>
    private static IEnumerable<Subscription> MergeAdjacentRuns(List<Subscription> runs, DateOnly asOf, SubscriptionDetectorOptions o)
    {
        var ordered = runs.OrderBy(r => r.FirstCharge).ToList();
        var merged = new List<Subscription>();
        foreach (var run in ordered)
        {
            var prev = merged.LastOrDefault(m => m.Cadence == run.Cadence && m.LastCharge < run.FirstCharge);
            if (prev is not null)
            {
                var (_, period, tolerance, _) = Cadences.First(c => c.Cadence == run.Cadence);
                var gap = (run.FirstCharge.ToDateTime(TimeOnly.MinValue) - prev.LastCharge.ToDateTime(TimeOnly.MinValue)).TotalDays;
                if (IsMultiple(gap, period, tolerance))
                {
                    var charges = prev.Charges.Concat(run.Charges).OrderBy(c => c.PostedOn).ToList();
                    merged.Remove(prev);
                    merged.Add(new Subscription
                    {
                        VendorKey = run.VendorKey, Vendor = run.Vendor, Cadence = run.Cadence,
                        TypicalAmount = Median(charges.Select(c => c.Amount)),
                        LatestAmount = run.LatestAmount, AnnualCost = run.AnnualCost,
                        FirstCharge = prev.FirstCharge, LastCharge = run.LastCharge, IsActive = run.IsActive,
                        Accounts = string.Join(", ", charges.Select(c => c.AccountName).Distinct(StringComparer.OrdinalIgnoreCase)),
                        CategoryName = run.CategoryName ?? prev.CategoryName,
                        Charges = charges,
                    });
                    continue;
                }
            }
            merged.Add(run);
        }
        return merged;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 0) return 0;
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 0) return 0;
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>"NETFLIX COM" → "Netflix Com"; the key is upper-cased and noise-stripped, which is right for grouping but not for reading.</summary>
    public static string DisplayName(string key) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant());

    public static string CadenceLabel(SubscriptionCadence c) => c switch
    {
        SubscriptionCadence.Monthly => "monthly",
        SubscriptionCadence.Quarterly => "quarterly",
        _ => "yearly",
    };
}
