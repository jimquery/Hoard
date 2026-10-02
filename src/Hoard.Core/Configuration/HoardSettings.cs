using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Security;

namespace Hoard.Core.Configuration;

/// <summary>
/// Settings shared by the desktop app and the background service. Persisted as JSON at
/// %ProgramData%\Hoard\hoard.json. The database password is never stored in plaintext:
/// it is DPAPI-protected (see <see cref="DatabaseSettings.ProtectedPassword"/>).
/// </summary>
public sealed class HoardSettings
{
    public DatabaseSettings Database { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public sealed class UiSettings
{
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Accent color as #RRGGBB, or null to follow the Windows accent color.</summary>
    public string? AccentColor { get; set; }
    /// <summary>"Mica", "Acrylic" or "None".</summary>
    public string Backdrop { get; set; } = "Mica";
}

public sealed class DatabaseSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string DatabaseName { get; set; } = "hoard";
    public string User { get; set; } = "hoard";
    /// <summary>Base64 of the DPAPI-protected UTF-8 password. Null until configured.</summary>
    public string? ProtectedPassword { get; set; }
    public bool UseSsl { get; set; } = false;

    public bool IsConfigured => !string.IsNullOrEmpty(ProtectedPassword);
}

public sealed class SyncSettings
{
    /// <summary>How often the background service refreshes every enabled institution.</summary>
    public int IntervalMinutes { get; set; } = 240;
    /// <summary>How far back to request transactions on the first sync of an institution.</summary>
    public int InitialHistoryDays { get; set; } = 365;
    /// <summary>Overlap window on incremental syncs so late-posting transactions are not missed.</summary>
    public int IncrementalOverlapDays { get; set; } = 14;
    public bool IncludePending { get; set; } = false;
}

public sealed class SecuritySettings
{
    /// <summary>
    /// "CurrentUser" (default): only the Windows user who configured Hoard can decrypt secrets.
    /// The background service must then run under that same user account.
    /// "LocalMachine": any process on this machine can decrypt; use only if the service must run as a
    /// system account. Changing this after setup requires re-entering credentials.
    /// </summary>
    public string ProtectionScope { get; set; } = "CurrentUser";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hoard");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "hoard.json");

    public static HoardSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new HoardSettings();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HoardSettings>(json, JsonOptions) ?? new HoardSettings();
    }

    public static void Save(HoardSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Builds a MySqlConnector connection string, unprotecting the password in memory only.</summary>
    public static string BuildConnectionString(DatabaseSettings db, ISecretProtector protector)
    {
        if (!db.IsConfigured)
            throw new InvalidOperationException("Database password has not been configured.");
        var password = System.Text.Encoding.UTF8.GetString(
            protector.Unprotect(Convert.FromBase64String(db.ProtectedPassword!)));
        return BuildConnectionString(db, password);
    }

    public static string BuildConnectionString(DatabaseSettings db, string password)
    {
        var b = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = db.Host,
            Port = (uint)db.Port,
            Database = db.DatabaseName,
            UserID = db.User,
            Password = password,
            SslMode = db.UseSsl ? MySqlConnector.MySqlSslMode.Required : MySqlConnector.MySqlSslMode.Preferred,
            AllowUserVariables = true,
            UseAffectedRows = true,
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 120,
        };
        return b.ConnectionString;
    }

    public static void SetProtectedPassword(DatabaseSettings db, string password, ISecretProtector protector)
    {
        db.ProtectedPassword = Convert.ToBase64String(
            protector.Protect(System.Text.Encoding.UTF8.GetBytes(password)));
    }
}
