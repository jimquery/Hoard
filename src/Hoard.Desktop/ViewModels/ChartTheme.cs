using System.Globalization;
using Hoard.Core.Reporting;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
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
        Labeler = v => v.ToString("C0", CultureInfo.CurrentCulture),
        LabelsPaint = new SolidColorPaint(Muted),
        SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 },
        TextSize = 12,
    };

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
/// They live in application resources and every chart picks them up through the implicit styles in App.xaml
/// (DynamicResource), so one call re-themes every open chart when light/dark changes.
/// </summary>
public static class ChartResources
{
    public const string LegendText = "ChartLegendTextPaint";
    public const string TooltipText = "ChartTooltipTextPaint";
    public const string TooltipBackground = "ChartTooltipBackgroundPaint";

    public static void Update(bool isDark)
    {
        if (System.Windows.Application.Current is not { } app) return;
        var ink = isDark ? SKColor.Parse("#f3f3f1") : SKColor.Parse("#1b1b1a");
        app.Resources[LegendText] = new SolidColorPaint(isDark ? SKColor.Parse("#d6d5cf") : SKColor.Parse("#3b3a37"));
        app.Resources[TooltipText] = new SolidColorPaint(ink);
        app.Resources[TooltipBackground] = new SolidColorPaint(isDark ? SKColor.Parse("#2c2c2a") : SKColor.Parse("#ffffff"));
    }
}
