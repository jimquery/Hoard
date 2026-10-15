using System.Globalization;
using System.Windows;
using Hoard.Core.Reporting;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// Theme-aware paints and series factories shared by the dashboard and reports. Categorical colors follow the
/// validated palette order (blue, orange, aqua, yellow, magenta, green, violet, red), with dark-surface steps.
/// </summary>
public sealed class ChartTheme
{
    private readonly ThemeService _theme;
    public ChartTheme(ThemeService theme) => _theme = theme;

    private static readonly SKColor[] LightPalette =
    [
        SKColor.Parse("#2a78d6"), SKColor.Parse("#eb6834"), SKColor.Parse("#1baf7a"), SKColor.Parse("#eda100"),
        SKColor.Parse("#e87ba4"), SKColor.Parse("#008300"), SKColor.Parse("#4a3aa7"), SKColor.Parse("#e34948"),
    ];
    private static readonly SKColor[] DarkPalette =
    [
        SKColor.Parse("#3987e5"), SKColor.Parse("#d95926"), SKColor.Parse("#199e70"), SKColor.Parse("#c98500"),
        SKColor.Parse("#d55181"), SKColor.Parse("#008300"), SKColor.Parse("#9085e9"), SKColor.Parse("#e66767"),
    ];

    // A wider categorical set for charts that stack many series (net worth by account type): hues spread around the wheel.
    private static readonly SKColor[] LightWide =
    [
        SKColor.Parse("#2a78d6"), SKColor.Parse("#eb6834"), SKColor.Parse("#1baf7a"), SKColor.Parse("#eda100"),
        SKColor.Parse("#8e44ad"), SKColor.Parse("#e34948"), SKColor.Parse("#16a2b8"), SKColor.Parse("#7f8c1a"),
        SKColor.Parse("#e87ba4"), SKColor.Parse("#5d4037"), SKColor.Parse("#4a3aa7"), SKColor.Parse("#008300"),
    ];
    private static readonly SKColor[] DarkWide =
    [
        SKColor.Parse("#3987e5"), SKColor.Parse("#d95926"), SKColor.Parse("#199e70"), SKColor.Parse("#c98500"),
        SKColor.Parse("#a569bd"), SKColor.Parse("#e66767"), SKColor.Parse("#22b8d1"), SKColor.Parse("#9aa62e"),
        SKColor.Parse("#d55181"), SKColor.Parse("#8d6e63"), SKColor.Parse("#9085e9"), SKColor.Parse("#2e9e2e"),
    ];
    public SKColor[] WidePalette => IsDark ? DarkWide : LightWide;

    public bool IsDark => _theme.IsDark;
    public SKColor[] Palette => IsDark ? DarkPalette : LightPalette;
    public SKColor Ink => IsDark ? SKColor.Parse("#ffffff") : SKColor.Parse("#0b0b0b");
    public SKColor Muted => IsDark ? SKColor.Parse("#c3c2b7") : SKColor.Parse("#52514e");
    public SKColor Grid => IsDark ? SKColor.Parse("#3a3a38") : SKColor.Parse("#e6e5e2");
    public SKColor Surface => IsDark ? SKColor.Parse("#1a1a19") : SKColor.Parse("#fcfcfb");

    public SKColor Slot(int index) => Palette[index % Palette.Length];

    /// <summary>A configured type color (#RRGGBB) or the palette slot when none is set.</summary>
    public SKColor ColorFor(string? hex, int fallbackIndex) =>
        !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c) ? c : Slot(fallbackIndex);

    public Axis CurrencyAxis() => new()
    {
        Labeler = CompactCurrency,
        LabelsPaint = new SolidColorPaint(Muted),
        SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 },
        TextSize = 12,
    };

    /// <summary>Axis label: $950, $12K, $1.2M - full figures crowd each other once balances reach six digits.</summary>
    public static string CompactCurrency(double value)
    {
        var abs = Math.Abs(value);
        if (abs < 10_000) return value.ToString("C0", CultureInfo.CurrentCulture);
        var (scaled, suffix) = abs >= 1e9 ? (abs / 1e9, "B") : abs >= 1e6 ? (abs / 1e6, "M") : (abs / 1e3, "K");
        var sign = value < 0 ? CultureInfo.CurrentCulture.NumberFormat.NegativeSign : "";
        return $"{sign}{CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol}{scaled.ToString("0.#", CultureInfo.CurrentCulture)}{suffix}";
    }

    public Axis CategoryAxis(string[] labels, double rotation = 0, bool everyLabel = false) => new()
    {
        Labels = labels,
        LabelsRotation = rotation,
        LabelsPaint = new SolidColorPaint(Muted),
        SeparatorsPaint = null,
        TextSize = 12,
        // Force one tick per category so no label is skipped when space is tight.
        MinStep = everyLabel ? 1 : 0,
        ForceStepToMin = everyLabel,
    };

    public Axis DateAxis(int spanDays)
    {
        var format = spanDays > 400 ? "MMM yy" : "MMM d";
        return new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString(format, CultureInfo.CurrentCulture))
        {
            LabelsPaint = new SolidColorPaint(Muted),
            SeparatorsPaint = null,
            TextSize = 12,
        };
    }

    public LineSeries<DateTimePoint> Line(string name, IEnumerable<DateTimePoint> values, SKColor color, float thickness = 2, bool markers = false) => new()
    {
        Name = name,
        Values = values.ToList(),
        Stroke = new SolidColorPaint(color) { StrokeThickness = thickness },
        Fill = null,
        GeometrySize = markers ? 8 : 0,
        GeometryFill = markers ? new SolidColorPaint(color) : null,
        GeometryStroke = markers ? new SolidColorPaint(Surface) { StrokeThickness = 1 } : null,
        LineSmoothness = 0,
        YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
    };

    public ColumnSeries<double> Column(string name, IEnumerable<double> values, SKColor color) => new()
    {
        Name = name,
        Values = values.ToList(),
        Fill = new SolidColorPaint(color),
        Stroke = null,
        MaxBarWidth = 40,
        Rx = 4, Ry = 4,
        YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
    };

    /// <summary>Suggested pixel height for a horizontal bar chart with <paramref name="rows"/> categories.</summary>
    public static double BarChartHeight(int rows) => Math.Max(320, 40 * rows + 70);

    /// <summary>Horizontal bars, one per account type, each in its configured color (a series per type so colors follow entities).</summary>
    public static ISeries[] TypeBars(IReadOnlyList<TypeBalanceGroup> groups, ChartTheme theme)
    {
        var series = new List<ISeries>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var values = new double?[groups.Count];
            values[i] = (double)g.Total;
            series.Add(new RowSeries<double?>
            {
                Name = g.Name,
                Values = values,
                Fill = new SolidColorPaint(theme.ColorFor(g.Color, i)),
                Stroke = null,
                MaxBarWidth = 28,
                Rx = 4, Ry = 4,
                IgnoresBarPosition = true,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
                IsVisibleAtLegend = false,
            });
        }
        return series.ToArray();
    }
}

/// <summary>
/// The chart chrome that LiveCharts does not take from series: legend and tooltip text and the tooltip background.
/// They live in application resources and every chart references them dynamically, so one call re-themes every open
/// chart when light/dark changes.
/// </summary>
/// <remarks>
/// The references are attached when a chart loads, not through implicit styles: a style is applied while LiveCharts'
/// constructor is still running, and its text-paint change handler throws a NullReferenceException at that point.
/// </remarks>
public static class ChartResources
{
    public const string LegendText = "ChartLegendTextPaint";
    public const string TooltipText = "ChartTooltipTextPaint";
    public const string TooltipBackground = "ChartTooltipBackgroundPaint";
    private const double TextSize = 13;

    /// <summary>Call once at startup, before any chart is created.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(CartesianChart), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnChartLoaded));
        EventManager.RegisterClassHandler(typeof(PieChart), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnChartLoaded));
    }

    private static void OnChartLoaded(object sender, RoutedEventArgs e)
    {
        var chart = (System.Windows.Controls.Control)sender;
        // LiveCharts paints its own theme's background (white unless Windows itself is dark); let the card show through.
        if (chart.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty) == DependencyProperty.UnsetValue)
            chart.Background = System.Windows.Media.Brushes.Transparent;
        // Loaded fires again on every page switch; a value the XAML set itself also wins, as it did over the style.
        Reference(chart, CartesianChart.LegendTextPaintProperty, LegendText);
        Reference(chart, CartesianChart.TooltipTextPaintProperty, TooltipText);
        Reference(chart, CartesianChart.TooltipBackgroundPaintProperty, TooltipBackground);
        if (chart.ReadLocalValue(CartesianChart.LegendTextSizeProperty) == DependencyProperty.UnsetValue)
            chart.SetValue(CartesianChart.LegendTextSizeProperty, TextSize);
        if (chart.ReadLocalValue(CartesianChart.TooltipTextSizeProperty) == DependencyProperty.UnsetValue)
            chart.SetValue(CartesianChart.TooltipTextSizeProperty, TextSize);
    }

    private static void Reference(FrameworkElement chart, DependencyProperty property, string key)
    {
        if (chart.ReadLocalValue(property) == DependencyProperty.UnsetValue)
            chart.SetResourceReference(property, key);
    }

    public static void Update(bool isDark)
    {
        if (Application.Current is not { } app) return;
        var ink = isDark ? SKColor.Parse("#f3f3f1") : SKColor.Parse("#1b1b1a");
        app.Resources[LegendText] = new SolidColorPaint(isDark ? SKColor.Parse("#d6d5cf") : SKColor.Parse("#3b3a37"));
        app.Resources[TooltipText] = new SolidColorPaint(ink);
        app.Resources[TooltipBackground] = new SolidColorPaint(isDark ? SKColor.Parse("#2c2c2a") : SKColor.Parse("#ffffff"));
    }
}
