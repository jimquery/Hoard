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
    public AdvisorSettings Advisor { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public WeeklyReportSettings WeeklyReport { get; set; } = new();
}

/// <summary>Outgoing mail (SMTP) for the weekly report. The password is sealed in the database (see <see cref="PasswordSetting"/>).</summary>
public sealed class EmailSettings
{
    public const string PasswordSetting = "email.smtp_password";
    public const string GmailHost = "smtp.gmail.com";

    public string SmtpHost { get; set; } = GmailHost;
    /// <summary>587 with STARTTLS (Gmail, Outlook); 465 implicit TLS is not supported by the .NET mail client.</summary>
    public int SmtpPort { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    /// <summary>Usually the full email address.</summary>
    public string? Username { get; set; }
    /// <summary>Blank = the username.</summary>
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Hoard";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(Username);
    [JsonIgnore]
    public string EffectiveFrom => string.IsNullOrWhiteSpace(FromAddress) ? Username ?? "" : FromAddress!;
}

/// <summary>The weekly spending email: when it goes out, to whom, and whether Claude's analysis is included.</summary>
public sealed class WeeklyReportSettings
{
    public bool Enabled { get; set; }
    /// <summary>The report sent on this day covers the seven days before it (Monday = the previous Monday to Sunday).</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    /// <summary>Local time of day, "HH:mm".</summary>
    public string Time { get; set; } = "07:00";
    public List<string> Recipients { get; set; } = new();
    /// <summary>Ask Claude (the AI advisor's key and model) for trends and ways to spend less. Needs the advisor turned on.</summary>
    public bool IncludeAiAnalysis { get; set; } = true;
    /// <summary>When the last scheduled (or "send now") report went out, UTC.</summary>
    public DateTime? LastSentUtc { get; set; }
    /// <summary>The scheduled occurrence (local) the last automatic send was for, so a restart never sends it twice.</summary>
    public DateTime? LastScheduledFor { get; set; }
    /// <summary>The last scheduled attempt, UTC; a failed send is retried an hour later.</summary>
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>AI advisor choices. The Anthropic API key itself is sealed in the database, not stored here.</summary>
public sealed class AdvisorSettings
{
    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high"];

    /// <summary>The user read what the advisor sends to Anthropic and turned it on.</summary>
    public bool Accepted { get; set; }
    /// <summary>Also share merchant names and individual transactions (search, top merchants, subscriptions), not only totals.</summary>
    public bool AllowTransactionDetail { get; set; } = true;
    /// <summary>"low", "medium" or "high": how hard the model thinks (and how much an answer costs).</summary>
    public string Effort { get; set; } = "high";
    /// <summary>Claude model id; blank = the default.</summary>
    public string? Model { get; set; }
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
    /// <summary>The desktop app syncs every institution once a day at <see cref="DailySyncTime"/> while it is running.</summary>
    public bool DailySyncEnabled { get; set; } = true;
    /// <summary>Local time of day, "HH:mm". If the app was closed then, it syncs soon after it next starts that day.</summary>
    public string DailySyncTime { get; set; } = "07:00";
    /// <summary>The scheduled time (local) of the last automatic daily sync, so it runs once per day.</summary>
    public DateTime? LastDailySyncFor { get; set; }
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
