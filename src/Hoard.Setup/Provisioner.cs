using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Security;
using MySqlConnector;

namespace Hoard.Setup;

/// <summary>
/// Everything that happens once the private MySQL server is running: the root password, Hoard's
/// database and least-privilege user, hoard.json, and the schema migrations.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Provisioner
{
    private const string RootUser = "root";

    /// <summary>
    /// Ensures root has a password we know. On a freshly initialized server root has none
    /// (--initialize-insecure), so one is generated and sealed with DPAPI straight away.
    /// Returns null when the server was already initialized and the sealed password has been lost,
    /// in which case the caller falls back to Hoard's own database user.
    /// </summary>
    public static async Task<string?> EnsureRootPasswordAsync(SetupContext ctx, bool freshlyInitialized)
    {
        var stored = ReadAdminPassword(ctx);

        if (!freshlyInitialized && stored is not null)
        {
            try
            {
                await MySqlInstance.WaitUntilReadyAsync(ctx, RootUser, stored, TimeSpan.FromSeconds(90));
                Log.Info("Signed in to MySQL with the stored administrator password.");
                return stored;
            }
            catch (Exception ex)
            {
                Log.Warn("The stored MySQL administrator password no longer works: " + ex.Message);
            }
        }

        if (freshlyInitialized)
        {
            await MySqlInstance.WaitUntilReadyAsync(ctx, RootUser, "", TimeSpan.FromSeconds(120));
            var generated = GeneratePassword();
            await using (var conn = new MySqlConnection(MySqlInstance.AdminConnectionString(ctx, RootUser, "")))
            {
                await conn.OpenAsync();
                var escaped = MySqlHelper.EscapeString(generated);
                await conn.ExecuteAsync($"ALTER USER '{RootUser}'@'localhost' IDENTIFIED BY '{escaped}'");
                await conn.ExecuteAsync("FLUSH PRIVILEGES");
            }
            WriteAdminPassword(ctx, generated);
            Log.Info("Set a generated MySQL administrator password and sealed it with DPAPI.");
            return generated;
        }

        if (stored is null)
        {
            Log.Warn(
                "MySQL is already initialized but Hoard has no administrator password for it. " +
                "The installer will use Hoard's own database user instead; the demo-database feature " +
                "will ask you for an administrator login when you use it.");
        }
        return null;
    }

    /// <summary>
    /// Creates (or refreshes) hoard.json so the desktop app and the service can both open the database.
    /// An existing working configuration is left completely alone.
    /// </summary>
    public static async Task<ConfigureResult> ConfigureDatabaseAsync(SetupContext ctx, string? rootPassword)
    {
        var settings = SettingsStore.Load();

        if (settings.Database.IsConfigured &&
            !string.Equals(settings.Security.ProtectionScope, "LocalMachine", StringComparison.OrdinalIgnoreCase))
        {
            // A pre-existing manual install sealed its secrets to one Windows account. Re-sealing them
            // to the machine here would make every stored bank credential undecryptable, so don't.
            Log.Warn(
                "Hoard is already configured with the CurrentUser protection scope, so its settings and " +
                "secrets belong to one Windows account. The installer left hoard.json untouched and will " +
                "install the sync service without starting it — set the service's log-on account to that " +
                "user and start it by hand (see README.md).");
            return new ConfigureResult(settings, Reused: true, ServiceCanRunAsLocalSystem: false);
        }

        if (settings.Database.IsConfigured)
        {
            try
            {
                var protector = DpapiSecretProtector.FromScopeName("LocalMachine");
                var existing = SettingsStore.BuildConnectionString(settings.Database, protector);
                var version = await DatabaseProvisioner.TestConnectionAsync(existing);
                Log.Info($"Existing hoard.json still works (MySQL {version}); keeping it.");
                return new ConfigureResult(settings, Reused: true, ServiceCanRunAsLocalSystem: true);
            }
            catch (Exception ex)
            {
                Log.Warn("The existing hoard.json could not be used, so it will be rewritten: " + ex.Message);
            }
        }

        if (rootPassword is null)
        {
            throw new InvalidOperationException(
                "Hoard's database settings need to be created, but no MySQL administrator password is " +
                "available. Delete " + ctx.MySqlDataDir + " to start from a clean database, or run " +
                "HoardSetup.exe install --root-password <password> from an elevated prompt.");
        }

        var appPassword = GeneratePassword();
        var db = new DatabaseSettings
        {
            Host = "127.0.0.1",
            Port = ctx.Port,
            DatabaseName = settings.Database.DatabaseName is { Length: > 0 } n ? n : "hoard",
            User = settings.Database.User is { Length: > 0 } u ? u : "hoard",
        };

        Log.Info($"Creating database '{db.DatabaseName}' and user '{db.User}'@'localhost'...");
        await DatabaseProvisioner.ProvisionAsync(
            "127.0.0.1", ctx.Port, RootUser, rootPassword, db.DatabaseName, db.User, appPassword);

        // DatabaseProvisioner grants 'user'@'localhost'. Connections arrive over TCP from 127.0.0.1 and
        // normally reverse-resolve to "localhost", but a machine with name resolution turned off (or a
        // stripped hosts file) would see the literal address instead, so grant that host as well.
        await GrantLoopbackAsync(ctx, rootPassword, db.DatabaseName, db.User, appPassword);

        var version2 = await DatabaseProvisioner.TestConnectionAsync(SettingsStore.BuildConnectionString(db, appPassword));
        Log.Info($"Connected as '{db.User}' to MySQL {version2}.");

        SettingsStore.SetProtectedPassword(db, appPassword, DpapiSecretProtector.FromScopeName("LocalMachine"));
        settings.Database = db;
        settings.Security.ProtectionScope = "LocalMachine";
        SettingsStore.Save(settings);
        Log.Info($"Wrote {SettingsStore.DefaultPath}");

        return new ConfigureResult(settings, Reused: false, ServiceCanRunAsLocalSystem: true);
    }

    private static async Task GrantLoopbackAsync(
        SetupContext ctx, string rootPassword, string databaseName, string appUser, string appPassword)
    {
        await using var conn = new MySqlConnection(MySqlInstance.AdminConnectionString(ctx, RootUser, rootPassword));
        await conn.OpenAsync();
        var db = MySqlHelper.EscapeString(databaseName);
        var user = MySqlHelper.EscapeString(appUser);
        var pwd = MySqlHelper.EscapeString(appPassword);
        await conn.ExecuteAsync($"CREATE USER IF NOT EXISTS '{user}'@'127.0.0.1' IDENTIFIED BY '{pwd}'");
        await conn.ExecuteAsync($"ALTER USER '{user}'@'127.0.0.1' IDENTIFIED BY '{pwd}'");
        await conn.ExecuteAsync(
            $"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES, DROP ON `{db}`.* TO '{user}'@'127.0.0.1'");
        await conn.ExecuteAsync("FLUSH PRIVILEGES");
    }

    public static async Task MigrateAsync(HoardSettings settings)
    {
        var protector = DpapiSecretProtector.FromScopeName(settings.Security.ProtectionScope);
        var factory = new MySqlConnectionFactory(SettingsStore.BuildConnectionString(settings.Database, protector));
        var applied = await new SchemaMigrator(factory).MigrateAsync();
        Log.Info(applied.Count == 0
            ? "Database schema is already up to date."
            : "Applied migrations: " + string.Join(", ", applied));
    }

    public static void RegisterHoardService(SetupContext ctx, bool startIt, bool dependsOnBundledMySql = true)
    {
        if (!File.Exists(ctx.ServiceExe))
        {
            Log.Warn($"{ctx.ServiceExe} is missing; skipping the sync service.");
            return;
        }

        EnsureEventLogSource();

        ServiceManager.CreateOrUpdate(
            SetupContext.HoardServiceName,
            SetupContext.HoardServiceDisplayName,
            $"\"{ctx.ServiceExe}\"",
            "Refreshes Hoard financial data in the background.",
            delayedAutoStart: startIt,
            manualStart: !startIt,
            dependsOn: dependsOnBundledMySql ? SetupContext.MySqlServiceName : null);

        if (startIt)
            ServiceManager.Start(SetupContext.HoardServiceName, TimeSpan.FromSeconds(60));
        else
            Log.Info($"Service '{SetupContext.HoardServiceName}' installed but not started.");
    }

    /// <summary>
    /// The sync service for a configuration whose secrets are sealed to one Windows account (CurrentUser scope):
    /// LocalSystem could never decrypt them, so the service is only useful under that account. If the user has
    /// already registered it that way, only the executable path is updated and it is restarted if it was running;
    /// otherwise it is created stopped and manual, with the README's instructions logged.
    /// </summary>
    public static void RegisterHoardServiceForUserScope(SetupContext ctx, bool wasRunning)
    {
        if (!File.Exists(ctx.ServiceExe))
        {
            Log.Warn($"{ctx.ServiceExe} is missing; skipping the sync service.");
            return;
        }

        EnsureEventLogSource();
        var binPath = $"\"{ctx.ServiceExe}\"";

        if (ServiceManager.Exists(SetupContext.HoardServiceName))
        {
            ServiceManager.SetBinaryPath(SetupContext.HoardServiceName, binPath);
            if (wasRunning) ServiceManager.Start(SetupContext.HoardServiceName, TimeSpan.FromSeconds(60));
            return;
        }

        ServiceManager.CreateOrUpdate(
            SetupContext.HoardServiceName,
            SetupContext.HoardServiceDisplayName,
            binPath,
            "Refreshes Hoard financial data in the background.",
            delayedAutoStart: false,
            manualStart: true,
            dependsOn: null);
        Log.Warn(
            $"Service '{SetupContext.HoardServiceName}' was created but not started: Hoard's secrets are sealed to " +
            "your Windows account, so the service can only run as that account. From an elevated prompt: " +
            $"sc.exe config {SetupContext.HoardServiceName} obj= \"DOMAIN\\user\" password= \"...\" start= delayed-auto " +
            $"&& sc.exe start {SetupContext.HoardServiceName}   (see README.md, Background service).");
    }

    private static void EnsureEventLogSource()
    {
        try
        {
            if (!EventLog.SourceExists(SetupContext.EventLogSource))
            {
                EventLog.CreateEventSource(new EventSourceCreationData(SetupContext.EventLogSource, "Application"));
                Log.Info($"Registered the '{SetupContext.EventLogSource}' Event Log source.");
            }
        }
        catch (Exception ex)
        {
            // Logging to the Event Log is a convenience; never fail an install over it.
            Log.Warn("Could not register the Event Log source: " + ex.Message);
        }
    }

    public static string? ReadAdminPassword(SetupContext ctx)
    {
        try
        {
            if (!File.Exists(ctx.AdminPasswordFile)) return null;
            var protector = DpapiSecretProtector.FromScopeName("LocalMachine");
            return Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(ctx.AdminPasswordFile)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {ctx.AdminPasswordFile}: {ex.Message}");
            return null;
        }
    }

    private static void WriteAdminPassword(SetupContext ctx, string password)
    {
        Directory.CreateDirectory(ctx.DataRoot);
        var protector = DpapiSecretProtector.FromScopeName("LocalMachine");
        File.WriteAllBytes(ctx.AdminPasswordFile, protector.Protect(Encoding.UTF8.GetBytes(password)));
        Acl.RestrictToAdministrators(ctx.AdminPasswordFile);
    }

    /// <summary>28 characters from an unambiguous alphabet — never typed by a human.</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(28, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}

public sealed record ConfigureResult(HoardSettings Settings, bool Reused, bool ServiceCanRunAsLocalSystem);
