using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Data.Demo;
using Hoard.Core.Security;

namespace Hoard.Desktop.ViewModels;

/// <summary>Creates the HoardDemo database (schema + sample data) and optionally switches the app to it.</summary>
public sealed partial class DemoDatabaseViewModel : ObservableObject
{
    private readonly HoardSettings _settings;
    private readonly ISecretProtector _protector;
    private readonly string _appPassword;

    public DemoDatabaseViewModel(HoardSettings settings, ISecretProtector protector, string appPassword)
    {
        _settings = settings;
        _protector = protector;
        _appPassword = appPassword;
    }

    public string ServerText => $"{_settings.Database.Host}:{_settings.Database.Port} (app user {_settings.Database.User})";

    [ObservableProperty] private string _demoName = DemoDatabaseBuilder.DefaultName;
    [ObservableProperty] private string _adminUser = "root";
    /// <summary>Set from the PasswordBox; never stored.</summary>
    public string AdminPassword { get; set; } = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCreated;
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(DemoName)) { Status = "Enter a database name."; return; }
        if (string.IsNullOrEmpty(_appPassword)) { Status = "The Hoard database password is not available; save the database settings first."; return; }
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(p => Status = p);
            var summary = await DemoDatabaseBuilder.CreateAsync(_settings.Database, _appPassword, AdminUser.Trim(), AdminPassword, DemoName.Trim(), _protector, progress);
            Status = $"Created {DemoName}: {summary.Members} household members, {summary.Institutions} institutions, {summary.Accounts} accounts, " +
                     $"{summary.Transactions:N0} transactions and {summary.Snapshots:N0} balance snapshots.";
            IsCreated = true;
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Points hoard.json at the demo database and restarts; the live database is untouched.</summary>
    [RelayCommand]
    private void SwitchAndRestart()
    {
        _settings.Database.DatabaseName = DemoName.Trim();
        SettingsStore.Save(_settings);
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
