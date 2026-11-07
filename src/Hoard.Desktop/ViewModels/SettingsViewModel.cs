using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Email;
using Hoard.Core.Scheduling;
using Hoard.Core.Security;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Settings page: appearance (theme, accent, backdrop) and sync options, all persisted to hoard.json.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HoardSettings _settings;
    private readonly ThemeService _theme;
    private readonly ISecretProtector _protector;
    private readonly SmtpEmailSender _email;

    /// <summary>Update checks and installs (Settings › Updates).</summary>
    public AppUpdater Updater { get; }

    public SettingsViewModel(HoardSettings settings, ThemeService theme, ISecretProtector protector, SmtpEmailSender email, AppUpdater updater)
    {
        Updater = updater;
        _settings = settings;
        _theme = theme;
        _protector = protector;
        _email = email;
        _dailySyncEnabled = settings.Sync.DailySyncEnabled;
        _dailySyncTime = Schedules.FormatTime(Schedules.ParseTime(settings.Sync.DailySyncTime, new TimeOnly(7, 0)));
        _smtpHost = settings.Email.SmtpHost;
        _smtpPort = settings.Email.SmtpPort;
        _smtpStartTls = settings.Email.UseStartTls;
        _smtpUser = settings.Email.Username ?? "";
        _smtpFrom = settings.Email.FromAddress ?? "";
        _smtpFromName = settings.Email.FromName;
        _testAddress = settings.Email.Username ?? "";
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
    /// <summary>The desktop app syncs every institution once a day at this time while it is open.</summary>
    [ObservableProperty] private bool _dailySyncEnabled;
    [ObservableProperty] private string _dailySyncTime;
    [ObservableProperty] private string _status = "";

    public string NextDailySyncText
    {
        get
        {
            if (!_settings.Sync.DailySyncEnabled) return "Automatic daily sync is off.";
            var at = Schedules.ParseTime(_settings.Sync.DailySyncTime, new TimeOnly(7, 0));
            var latest = Schedules.LatestDaily(DateTime.Now, at);
            var next = Schedules.IsDue(latest, _settings.Sync.LastDailySyncFor) ? DateTime.Now : Schedules.NextDaily(DateTime.Now, at);
            return (next <= DateTime.Now.AddMinutes(1) ? "Next automatic sync: within a minute" : $"Next automatic sync: {next:dddd h:mm tt}") +
                   (_settings.Sync.LastDailySyncFor is { } last ? $" · last automatic sync {last:ddd MMM d}." : ".");
        }
    }

    /// <summary>Called by the scheduler after an automatic sync, so the line under the setting stays current.</summary>
    public void RefreshSchedules() => OnPropertyChanged(nameof(NextDailySyncText));

    // ----- Outgoing email (weekly report) -----
    [ObservableProperty] private string _smtpHost;
    [ObservableProperty] private int _smtpPort;
    [ObservableProperty] private bool _smtpStartTls;
    [ObservableProperty] private string _smtpUser;
    [ObservableProperty] private string _smtpFrom;
    [ObservableProperty] private string _smtpFromName;
    [ObservableProperty] private string _testAddress;
    [ObservableProperty] private string _emailStatus = "";
    [ObservableProperty] private bool _emailBusy;
    [ObservableProperty] private bool _hasEmailPassword;
    /// <summary>Set from the PasswordBox; blank keeps the stored password.</summary>
    public string SmtpPassword { get; set; } = "";

    /// <summary>Reads whether a password is stored (it lives in the database, so this runs when the page opens).</summary>
    public async Task LoadEmailAsync()
    {
        try { HasEmailPassword = await _email.HasPasswordAsync(); }
        catch (Exception ex) { EmailStatus = ex.Message; }
    }

    [RelayCommand]
    private void UseGmail()
    {
        SmtpHost = EmailSettings.GmailHost;
        SmtpPort = 587;
        SmtpStartTls = true;
        EmailStatus = "Gmail: use your Gmail address as the username and an app password (myaccount.google.com/apppasswords; needs 2-Step Verification), not your normal password.";
    }

    private void ApplyEmail()
    {
        var e = _settings.Email;
        e.SmtpHost = SmtpHost.Trim();
        e.SmtpPort = Math.Clamp(SmtpPort, 1, 65535);
        e.UseStartTls = SmtpStartTls;
        e.Username = string.IsNullOrWhiteSpace(SmtpUser) ? null : SmtpUser.Trim();
        e.FromAddress = string.IsNullOrWhiteSpace(SmtpFrom) ? null : SmtpFrom.Trim();
        e.FromName = string.IsNullOrWhiteSpace(SmtpFromName) ? "Hoard" : SmtpFromName.Trim();
    }

    [RelayCommand]
    private async Task SaveEmailAsync()
    {
        EmailBusy = true;
        try
        {
            ApplyEmail();
            if (!string.IsNullOrEmpty(SmtpPassword)) await _email.SetPasswordAsync(SmtpPassword);
            SettingsStore.Save(_settings);
            HasEmailPassword = await _email.HasPasswordAsync();
            EmailStatus = HasEmailPassword ? "Email settings saved." : "Email settings saved. Enter the password too.";
        }
        catch (Exception ex) { EmailStatus = "Not saved — " + ex.Message; }
        finally { EmailBusy = false; }
    }

    [RelayCommand]
    private async Task SendTestEmailAsync()
    {
        EmailBusy = true;
        try
        {
            ApplyEmail();
            if (!string.IsNullOrEmpty(SmtpPassword)) await _email.SetPasswordAsync(SmtpPassword);
            SettingsStore.Save(_settings);
            HasEmailPassword = await _email.HasPasswordAsync();
            await _email.SendAsync([TestAddress], "Hoard test email",
                "<p style=\"font-family:Segoe UI,Arial,sans-serif\">Hoard can send email. The weekly spending report will arrive like this.</p>",
                "Hoard can send email. The weekly spending report will arrive like this.");
            EmailStatus = $"Test email sent to {TestAddress}. Settings saved.";
        }
        catch (Exception ex) { EmailStatus = ex.Message; }
        finally { EmailBusy = false; }
    }

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
        var at = Schedules.ParseTime(DailySyncTime, new TimeOnly(7, 0));
        var timeChanged = _settings.Sync.DailySyncTime != Schedules.FormatTime(at) || !_settings.Sync.DailySyncEnabled;
        _settings.Sync.DailySyncEnabled = DailySyncEnabled;
        _settings.Sync.DailySyncTime = Schedules.FormatTime(at);
        // A new time starts tomorrow if today's has already passed, instead of syncing the moment it is saved.
        if (DailySyncEnabled && timeChanged) _settings.Sync.LastDailySyncFor = Schedules.LatestDaily(DateTime.Now, at);
        DailySyncTime = _settings.Sync.DailySyncTime;
        OnPropertyChanged(nameof(NextDailySyncText));
        IntervalMinutes = _settings.Sync.IntervalMinutes;
        InitialHistoryDays = _settings.Sync.InitialHistoryDays;
        SettingsStore.Save(_settings);
        Status = "Sync settings saved. The automatic daily sync applies now; the background service picks up the rest on its next start.";
    }
}
