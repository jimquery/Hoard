using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Security;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Settings page: appearance (theme, accent, backdrop) and sync options, all persisted to hoard.json.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HoardSettings _settings;
    private readonly ThemeService _theme;
    private readonly ISecretProtector _protector;

    public SettingsViewModel(HoardSettings settings, ThemeService theme, ISecretProtector protector)
    {
        _settings = settings;
        _theme = theme;
        _protector = protector;
        _dbHost = settings.Database.Host;
        _dbPort = settings.Database.Port;
        _dbName = settings.Database.DatabaseName;
        _dbUser = settings.Database.User;
        _dbUseSsl = settings.Database.UseSsl;
        _selectedTheme = ThemeService.ThemeOptions.Contains(settings.Ui.Theme) ? settings.Ui.Theme : "System";
        _selectedAccent = ThemeService.AccentPresets.FirstOrDefault(p => string.Equals(p.Hex, settings.Ui.AccentColor, StringComparison.OrdinalIgnoreCase))
                          ?? ThemeService.AccentPresets[0];
        _selectedBackdrop = ThemeService.BackdropOptions.Contains(settings.Ui.Backdrop) ? settings.Ui.Backdrop : "Mica";
        _intervalMinutes = settings.Sync.IntervalMinutes;
        _initialHistoryDays = settings.Sync.InitialHistoryDays;
        _includePending = settings.Sync.IncludePending;
    }

    public IReadOnlyList<string> ThemeOptions => ThemeService.ThemeOptions;
    public IReadOnlyList<AccentPreset> AccentPresets => ThemeService.AccentPresets;
    public IReadOnlyList<string> BackdropOptions => ThemeService.BackdropOptions;

    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private AccentPreset _selectedAccent;
    [ObservableProperty] private string _selectedBackdrop;
    [ObservableProperty] private int _intervalMinutes;
    [ObservableProperty] private int _initialHistoryDays;
    [ObservableProperty] private bool _includePending;
    [ObservableProperty] private string _status = "";

    public string SettingsPath => SettingsStore.DefaultPath;
    public string DatabaseText => $"{_settings.Database.User}@{_settings.Database.Host}:{_settings.Database.Port}/{_settings.Database.DatabaseName}";

    // ----- Database connection (saved to hoard.json; takes effect after a restart) -----
    [ObservableProperty] private string _dbHost;
    [ObservableProperty] private int _dbPort;
    [ObservableProperty] private string _dbName;
    [ObservableProperty] private string _dbUser;
    [ObservableProperty] private bool _dbUseSsl;
    /// <summary>Set from the PasswordBox; blank keeps the stored password.</summary>
    public string DbPassword { get; set; } = "";
    [ObservableProperty] private string _dbStatus = "";
    [ObservableProperty] private bool _dbBusy;
    [ObservableProperty] private bool _restartNeeded;

    public HoardSettings Settings => _settings;
    public ISecretProtector Protector => _protector;

    private DatabaseSettings PendingDatabase() => new()
    {
        Host = DbHost.Trim(), Port = DbPort, DatabaseName = DbName.Trim(), User = DbUser.Trim(), UseSsl = DbUseSsl,
        ProtectedPassword = _settings.Database.ProtectedPassword,
    };

    /// <summary>The password to use: the one typed, else the stored one.</summary>
    public string CurrentDbPassword()
    {
        if (!string.IsNullOrEmpty(DbPassword)) return DbPassword;
        if (!_settings.Database.IsConfigured) return "";
        return System.Text.Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(_settings.Database.ProtectedPassword!)));
    }

    [RelayCommand]
    private async Task TestDatabaseAsync()
    {
        DbBusy = true;
        try
        {
            var version = await DatabaseProvisioner.TestConnectionAsync(SettingsStore.BuildConnectionString(PendingDatabase(), CurrentDbPassword()));
            DbStatus = $"Connected: MySQL {version}, database \"{DbName}\".";
        }
        catch (Exception ex) { DbStatus = "Connection failed: " + ex.Message; }
        finally { DbBusy = false; }
    }

    [RelayCommand]
    private async Task SaveDatabaseAsync()
    {
        DbBusy = true;
        try
        {
            var db = PendingDatabase();
            var password = CurrentDbPassword();
            if (string.IsNullOrEmpty(password)) { DbStatus = "Enter the database password."; return; }
            await DatabaseProvisioner.TestConnectionAsync(SettingsStore.BuildConnectionString(db, password));
            SettingsStore.SetProtectedPassword(db, password, _protector);
            _settings.Database = db;
            SettingsStore.Save(_settings);
            OnPropertyChanged(nameof(DatabaseText));
            RestartNeeded = true;
            DbStatus = "Database settings saved. Restart Hoard to use them (the background service picks them up on its next start).";
        }
        catch (Exception ex) { DbStatus = "Not saved — " + ex.Message; }
        finally { DbBusy = false; }
    }

    [RelayCommand]
    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    // Appearance changes apply immediately.
    partial void OnSelectedThemeChanged(string value) => ApplyAppearance();
    partial void OnSelectedAccentChanged(AccentPreset value) => ApplyAppearance();
    partial void OnSelectedBackdropChanged(string value) => ApplyAppearance();

    private void ApplyAppearance()
    {
        try
        {
            _theme.Update(SelectedTheme, SelectedAccent?.Hex, SelectedBackdrop);
            Status = "Appearance saved.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void SaveSync()
    {
        _settings.Sync.IntervalMinutes = Math.Clamp(IntervalMinutes, 15, 24 * 60);
        _settings.Sync.InitialHistoryDays = Math.Clamp(InitialHistoryDays, 30, 3650);
        _settings.Sync.IncludePending = IncludePending;
        IntervalMinutes = _settings.Sync.IntervalMinutes;
        InitialHistoryDays = _settings.Sync.InitialHistoryDays;
        SettingsStore.Save(_settings);
        Status = "Sync settings saved. The background service picks them up on its next start.";
    }
}
