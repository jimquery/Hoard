namespace Hoard.Core.Budgeting;

/// <summary>What 12 months of a category group's spending suggest about the next month.</summary>
/// <param name="Average">Plain mean of the history months (empty months count as zero).</param>
/// <param name="TypicalMonth">Mean with the single highest and lowest month left out (robust to one-off spikes); the plain mean when fewer than six months.</param>
/// <param name="MonthlyTrend">Least-squares slope of the history, in currency per month (positive = spending rising).</param>
/// <param name="TrendStrength">R² of that line, 0..1: how much of the month-to-month variation the trend explains.</param>
/// <param name="Predicted">The forecast for the next month.</param>
/// <param name="Low">Lower end of a roughly 80 % range around the forecast.</param>
/// <param name="High">Upper end of that range.</param>
/// <param name="SuggestedTarget">The forecast rounded up to a tidy figure, offered as a starting target.</param>
public sealed record BudgetForecast(
    decimal Average,
    decimal TypicalMonth,
    decimal MonthlyTrend,
    double TrendStrength,
    decimal Predicted,
    decimal Low,
    decimal High,
    int MonthsWithSpending,
    decimal SuggestedTarget)
{
    public static readonly BudgetForecast Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Trend as a share of the typical month, e.g. 0.03 = rising 3 % a month.</summary>
    public decimal TrendPercent => TypicalMonth == 0 ? 0 : MonthlyTrend / TypicalMonth;
}

/// <summary>
/// Pure forecasting for budgets. The forecast starts from the typical month (a trimmed mean, so one annual insurance
/// bill or a holiday does not set the baseline) and moves toward the trend line's next point only as far as the trend
/// is consistent: the blend weight is half the line's R², so a noisy history stays near its typical month and a steady
/// rise or fall moves the forecast up to halfway to the extrapolation. The range is the forecast ± 1.28 standard
/// deviations of the history (about 80 % of months for normally distributed spending), floored at zero.
/// </summary>
public static class BudgetForecaster
{
    public static BudgetForecast Forecast(IReadOnlyList<decimal> history)
    {
        var n = history.Count;
        if (n == 0 || history.All(h => h == 0)) return BudgetForecast.Empty;

        var average = history.Average();
        var typical = n >= 6
            ? (history.Sum() - history.Max() - history.Min()) / (n - 2)
            : average;

        // Least squares on x = 0..n-1.
        var xMean = (n - 1) / 2.0;
        var yMean = (double)average;
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - xMean;
            var dy = (double)history[i] - yMean;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        var slope = sxx == 0 ? 0 : sxy / sxx;
        var r2 = sxx == 0 || syy == 0 ? 0 : sxy * sxy / (sxx * syy);
        var nextOnLine = yMean + slope * (n - xMean);

        var weight = 0.5 * r2;
        var predicted = Math.Max(0, (double)typical + weight * (nextOnLine - (double)typical));
        var sd = n > 1 ? Math.Sqrt(syy / (n - 1)) : 0;
        var low = Math.Max(0, predicted - 1.28 * sd);
        var high = predicted + 1.28 * sd;

        return new BudgetForecast(
            Average: Round(average),
            TypicalMonth: Round(typical),
            MonthlyTrend: Round((decimal)slope),
            TrendStrength: Math.Round(r2, 3),
            Predicted: Round((decimal)predicted),
            Low: Round((decimal)low),
            High: Round((decimal)high),
            MonthsWithSpending: history.Count(h => h > 0),
            SuggestedTarget: Tidy((decimal)predicted));
    }

    /// <summary>
    /// Month-end estimate for a month in progress: what is already spent plus the forecast's share for the days left.
    /// Never below the amount already spent.
    /// </summary>
    public static decimal ProjectMonthEnd(decimal spentToDate, decimal predicted, int dayOfMonth, int daysInMonth)
    {
        if (daysInMonth <= 0) return spentToDate;
        var remaining = Math.Clamp(daysInMonth - dayOfMonth, 0, daysInMonth);
        return Round(spentToDate + predicted * remaining / daysInMonth);
    }

    /// <summary>Rounds up to $5 under $100, $10 under $1,000, $50 above.</summary>
    public static decimal Tidy(decimal amount)
    {
        if (amount <= 0) return 0;
        var step = amount < 100 ? 5m : amount < 1000 ? 10m : 50m;
        return Math.Ceiling(amount / step) * step;
    }

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
}
