namespace Hoard.Core.Reporting;

/// <summary>
/// Future-value math for the College Savings report: monthly compounding of the current balance plus a
/// level monthly contribution, over a whole number of months.
/// </summary>
public static class CollegeProjection
{
    /// <param name="currentBalance">Balance today.</param>
    /// <param name="monthlyContribution">Contribution added at the end of each month (0 for none).</param>
    /// <param name="annualRate">Nominal annual growth rate, e.g. 0.06 for 6%.</param>
    /// <param name="months">Months until the target date (0 returns the current balance).</param>
    public static decimal FutureValue(decimal currentBalance, decimal monthlyContribution, decimal annualRate, int months)
    {
        if (months <= 0) return currentBalance;
        var r = (double)annualRate / 12.0;
        var growth = Math.Pow(1 + r, months);
        var fv = (double)currentBalance * growth;
        if (monthlyContribution != 0)
            fv += (double)monthlyContribution * (Math.Abs(r) < 1e-12 ? months : (growth - 1) / r);
        return Math.Round((decimal)fv, 2);
    }

    /// <summary>Whole months from <paramref name="from"/> until <paramref name="to"/>, never negative.</summary>
    public static int MonthsBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;
        var months = (to.Year - from.Year) * 12 + (to.Month - from.Month);
        if (to.Day < from.Day) months--;
        return Math.Max(0, months);
    }
}
