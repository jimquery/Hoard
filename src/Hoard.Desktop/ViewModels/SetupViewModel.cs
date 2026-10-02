using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Security;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// First-run wizard. Uses a MySQL administrative login once to create the `hoard` database and a
/// least-privilege `hoard` user, then stores only the app user's password (DPAPI-protected) in hoard.json.
/// The admin password is never persisted.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly HoardSettings _settings;

    public SetupViewModel(HoardSettings settings)
    {
        _settings = settings;
        Host = settings.Database.Host;
        Port = settings.Database.Port;
        DatabaseName = settings.Database.DatabaseName;
        AppUser = settings.Database.User;
        AppPassword = GeneratePassword();
    }

    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 3306;
    [ObservableProperty] private string _adminUser = "root";
    [ObservableProperty] private string _databaseName = "hoard";
    [ObservableProperty] private string _appUser = "hoard";
    [ObservableProperty] private string _appPassword = "";
    [ObservableProperty] private string _status = "Enter your MySQL administrator login. Hoard will create its own database and a restricted user.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isComplete;

    /// <summary>Set by the view from a PasswordBox; never bound to a plain TextBox.</summary>
    public string AdminPassword { get; set; } = "";

    [RelayCommand]
    private async Task ProvisionAsync()
    {
        IsBusy = true;
        try
        {
            Status = "Creating database and user...";
            await DatabaseProvisioner.ProvisionAsync(Host, Port, AdminUser, AdminPassword, DatabaseName, AppUser, AppPassword);

            var db = new DatabaseSettings { Host = Host, Port = Port, DatabaseName = DatabaseName, User = AppUser };
            Status = "Testing connection as the Hoard user...";
            var version = await DatabaseProvisioner.TestConnectionAsync(SettingsStore.BuildConnectionString(db, AppPassword));

            var protector = DpapiSecretProtector.FromScopeName(_settings.Security.ProtectionScope);
            SettingsStore.SetProtectedPassword(db, AppPassword, protector);
            _settings.Database = db;
            SettingsStore.Save(_settings);

            Status = $"Connected to MySQL {version}. Settings saved to {SettingsStore.DefaultPath}.";
            IsComplete = true;
        }
        catch (Exception ex)
        {
            Status = "Setup failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(28, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}
