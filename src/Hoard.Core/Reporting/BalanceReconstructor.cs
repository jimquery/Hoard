namespace Hoard.Core.Reporting;

/// <summary>
/// Rebuilds one account's daily balance history from sparse balance snapshots plus daily transaction totals.
///
/// For a day D:
///   - if a snapshot exists on or before D, take the latest such snapshot S and add every transaction posted
///     after S up to and including D (forward roll);
///   - otherwise take the earliest snapshot after D and subtract every transaction posted after D up to and
///     including that snapshot's day (backward roll — this is how history before the first sync is estimated).
/// A day is "observed" only when a snapshot exists for that exact day; everything else is flagged estimated.
/// Pending transactions must be excluded by the caller: they are not yet reflected in bank balances.
/// </summary>
public static class BalanceReconstructor
{
    public static IReadOnlyList<DailyBalance> Reconstruct(
        IReadOnlyList<(DateOnly Date, decimal Balance)> snapshots,
        IReadOnlyDictionary<DateOnly, decimal> dailyTransactionTotals,
        DateOnly from,
        DateOnly to)
    {
        if (snapshots.Count == 0 || to < from) return [];

        var snaps = snapshots.OrderBy(s => s.Date).ToList();
        var days = to.DayNumber - from.DayNumber + 1;
        var result = new List<DailyBalance>(days);

        // Cumulative transaction total up to and including each day, over the union of the requested range and snapshot dates.
        var lo = Min(from, snaps[0].Date);
        var hi = Max(to, snaps[^1].Date);
        var cum = new decimal[hi.DayNumber - lo.DayNumber + 1];
        decimal running = 0m;
        for (var i = 0; i < cum.Length; i++)
        {
            var d = lo.AddDays(i);
            if (dailyTransactionTotals.TryGetValue(d, out var v)) running += v;
            cum[i] = running;
        }
        decimal CumAt(DateOnly d) => cum[d.DayNumber - lo.DayNumber];

        var snapIdx = 0; // index of the latest snapshot with Date <= current day, or -1 semantics via 'hasPrior'
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            while (snapIdx < snaps.Count && snaps[snapIdx].Date <= d) snapIdx++;
            var priorIndex = snapIdx - 1; // latest snapshot <= d

            if (priorIndex >= 0)
            {
                var s = snaps[priorIndex];
                if (s.Date == d)
                {
                    result.Add(new DailyBalance(d, s.Balance, IsEstimated: false));
                }
                else
                {
                    var balance = s.Balance + (CumAt(d) - CumAt(s.Date));
                    result.Add(new DailyBalance(d, balance, IsEstimated: true));
                }
            }
            else
            {
                var s = snaps[0]; // earliest snapshot, which is after d
                var balance = s.Balance - (CumAt(s.Date) - CumAt(d));
                result.Add(new DailyBalance(d, balance, IsEstimated: true));
            }
        }
        return result;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
