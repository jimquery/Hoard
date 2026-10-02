using System.Windows;
using System.Windows.Media;
using Hoard.Core.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Hoard.Desktop.Services;

public sealed record AccentPreset(string Name, string? Hex)
{
    public override string ToString() => Name;
}

/// <summary>
/// Applies the persisted UI theme (System/Light/Dark), accent color and window backdrop through WPF-UI,
/// and tells interested view models (charts) when the theme changes.
/// </summary>
public sealed class ThemeService
{
    private readonly HoardSettings _settings;

    public ThemeService(HoardSettings settings) => _settings = settings;

    public static IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];
    public static IReadOnlyList<string> BackdropOptions { get; } = ["Mica", "Acrylic", "None"];
    public static IReadOnlyList<AccentPreset> AccentPresets { get; } =
    [
        new("Windows accent", null),
        new("Blue", "#2a78d6"),
        new("Teal", "#1baf7a"),
        new("Green", "#008300"),
        new("Violet", "#4a3aa7"),
        new("Orange", "#eb6834"),
        new("Rose", "#e87ba4"),
        new("Red", "#e34948"),
        new("Gold", "#eda100"),
    ];

    public event Action? Changed;

    public string Theme => _settings.Ui.Theme;
    public string? AccentHex => _settings.Ui.AccentColor;
    public string Backdrop => _settings.Ui.Backdrop;
    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>Apply the persisted settings. Call once at start-up before any window is shown, and after changes.</summary>
    public void Apply()
    {
        var backdrop = ParseBackdrop(_settings.Ui.Backdrop);
        var customAccent = TryParseColor(_settings.Ui.AccentColor);

        switch (_settings.Ui.Theme)
        {
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop, updateAccent: customAccent is null);
                break;
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop, updateAccent: customAccent is null);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(updateAccent: customAccent is null);
                break;
        }

        if (customAccent is { } accent)
            ApplicationAccentColorManager.Apply(accent, ApplicationThemeManager.GetAppTheme(), systemGlassColor: false);

        Changed?.Invoke();
    }

    /// <summary>Follow Windows light/dark switches while the main window is open (only meaningful for Theme = System).</summary>
    public void WatchSystem(Window window)
    {
        if (_settings.Ui.Theme == "System")
            SystemThemeWatcher.Watch(window, ParseBackdrop(_settings.Ui.Backdrop), updateAccents: _settings.Ui.AccentColor is null);
    }

    public void Update(string theme, string? accentHex, string backdrop)
    {
        _settings.Ui.Theme = ThemeOptions.Contains(theme) ? theme : "System";
        _settings.Ui.AccentColor = TryParseColor(accentHex) is null ? null : accentHex;
        _settings.Ui.Backdrop = BackdropOptions.Contains(backdrop) ? backdrop : "Mica";
        SettingsStore.Save(_settings);
        Apply();
    }

    public static WindowBackdropType ParseBackdrop(string? name) => name switch
    {
        "Acrylic" => WindowBackdropType.Acrylic,
        "None" => WindowBackdropType.None,
        _ => WindowBackdropType.Mica,
    };

    public static Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return null; }
    }
}
