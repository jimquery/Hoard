using System.Windows;
using System.Windows.Input;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;

namespace Hoard.Desktop.Controls;

/// <summary>What a click on a chart landed on.</summary>
/// <param name="Exact">The point whose shape is under the pointer (a stacked segment, a pie slice, a bar), or null.</param>
/// <param name="Near">The nearest point by the chart's own strategy (same month column, same bar row), or null.</param>
public sealed record ChartHit(ChartPoint? Exact, ChartPoint? Near)
{
    /// <summary>The point to take an index (month, row) from: the exact one when there is one.</summary>
    public ChartPoint? Point => Exact ?? Near;
    /// <summary>Name of the series under the pointer, only when the click landed on its shape.</summary>
    public string? ExactSeriesName => Exact?.Context.Series.Name;
}

/// <summary>
/// Chart click → view-model command, with a real hit test. LiveCharts raises its own pointer-down events on every
/// series the chart's finding strategy matches - with CompareOnlyX that is every stacked series in the clicked month,
/// so per-series handlers all fire and the last one wins. This asks the chart for the point whose shape is actually
/// under the pointer (ExactMatch) and, separately, for the nearest point by <see cref="NearStrategyProperty"/>, and
/// passes both to the command as a <see cref="ChartHit"/>. Clicks that hit nothing are ignored.
/// </summary>
public static class ChartClick
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(ChartClick), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject d) => (ICommand?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, ICommand? value) => d.SetValue(CommandProperty, value);

    /// <summary>How to find the "near" point when the click misses every shape (default CompareOnlyX).</summary>
    public static readonly DependencyProperty NearStrategyProperty = DependencyProperty.RegisterAttached(
        "NearStrategy", typeof(FindingStrategy), typeof(ChartClick), new PropertyMetadata(FindingStrategy.CompareOnlyX));

    public static FindingStrategy GetNearStrategy(DependencyObject d) => (FindingStrategy)d.GetValue(NearStrategyProperty);
    public static void SetNearStrategy(DependencyObject d, FindingStrategy value) => d.SetValue(NearStrategyProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.MouseLeftButtonUp -= OnMouseUp;
        if (e.NewValue is not null) element.MouseLeftButtonUp += OnMouseUp;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element is not IChartView chart) return;
        if (GetCommand(element) is not { } command) return;
        var position = e.GetPosition(element);
        var at = new LvcPointD(position.X, position.Y);
        var hit = Hit(chart, at);
        if (hit.Point is null) return;
        if (command.CanExecute(hit)) command.Execute(hit);
    }

    /// <summary>Exact and near points at a position in chart coordinates.</summary>
    public static ChartHit Hit(IChartView chart, LvcPointD at)
    {
        ChartPoint? exact = null, near = null;
        try
        {
            exact = chart.GetPointsAt(at, FindingStrategy.ExactMatch, FindPointFor.PointerDownEvent).FirstOrDefault();
            if (chart is FrameworkElement fe)
                near = chart.GetPointsAt(at, GetNearStrategy(fe), FindPointFor.PointerDownEvent).FirstOrDefault();
        }
        catch (Exception)
        {
            // Hit testing a chart that is mid-update can throw inside LiveCharts; a missed click is harmless.
        }
        return new ChartHit(exact, near);
    }
}
