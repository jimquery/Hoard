using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Hoard.Core.Configuration;

namespace Hoard.Setup;

/// <summary>
/// The Hoard installer's worker. The MSI invokes it as a deferred custom action running as
/// LocalSystem — with a verb and nothing else, so that no Windows Installer property has to survive
/// the trip into deferred execution. It is equally usable by hand from an elevated prompt, which is
/// how you repair a broken install:
///
///   HoardSetup.exe install [--port 3307] [--root-password &lt;pwd&gt;] [--no-service]
///   HoardSetup.exe uninstall [--purge]
///   HoardSetup.exe stop
///   HoardSetup.exe status
///   HoardSetup.exe show-admin-password
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var verb = (args.FirstOrDefault() ?? "help").ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        if (verb is "help" or "-h" or "--help" or "/?")
        {
            PrintUsage();
            return 0;
        }

        Log.Open(Path.Combine(SettingsStore.DefaultDirectory, "logs"), verb);
        try
        {
            Log.Info($"HoardSetup {verb} starting (running as {Environment.UserName}).");
            switch (verb)
            {
                case "install":
                    await InstallAsync(options);
                    return 0;
                case "uninstall":
                    Uninstall(options);
                    return 0;
                case "stop":
                    StopServices();
                    return 0;
                case "status":
                    Status();
                    return 0;
                case "show-admin-password":
                    return ShowAdminPassword();
                default:
                    Log.Error($"Unknown verb '{verb}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"HoardSetup {verb} failed.", ex);
            // An uninstall must always be allowed to finish, or the product can never be removed.
            return verb == "uninstall" ? 0 : 1;
        }
        finally
        {
            Log.Info($"HoardSetup {verb} finished. Log: {Log.Path}");
            Log.Close();
        }
    }

    private static async Task InstallAsync(Options options)
    {
        var port = options.Port ?? ReadConfiguredPort() ?? SetupContext.DefaultPort;
        var ctx = SetupContext.Create(port);

        Log.Info($"Install root: {ctx.InstallRoot}");
        Log.Info($"Data root:    {ctx.DataRoot}");
        Log.Info($"MySQL port:   {ctx.Port}");

        if (!File.Exists(ctx.MySqlDaemon))
            throw new FileNotFoundException($"The bundled MySQL server is missing: {ctx.MySqlDaemon}");

        Directory.CreateDirectory(ctx.DataRoot);

        // A running sync service would hold connections to a database that is about to be reconfigured.
        var syncWasRunning = ServiceManager.IsRunning(SetupContext.HoardServiceName);
        ServiceManager.StopIfRunning(SetupContext.HoardServiceName, TimeSpan.FromSeconds(60));

        // "Bring your own database": hoard.json already points somewhere other than the bundled server (a
        // development set-up on the user's own MySQL, say). Keep using it, and never start a second MySQL.
        var existing = SettingsStore.Load();
        if (existing.Database.IsConfigured && !IsBundledInstance(existing.Database, ctx.Port))
        {
            await InstallAgainstExternalDatabaseAsync(ctx, existing, options, syncWasRunning);
            return;
        }

        var freshlyInitialized = !MySqlInstance.IsInitialized(ctx);
        MySqlInstance.WriteConfig(ctx);
        MySqlInstance.Initialize(ctx);
        MySqlInstance.RegisterService(ctx);
        ServiceManager.Start(SetupContext.MySqlServiceName, TimeSpan.FromSeconds(120));
        // The service reports Running before mysqld opens its port; wait for the port itself.
        await MySqlInstance.WaitUntilListeningAsync(ctx, TimeSpan.FromSeconds(180));

        var rootPassword = options.RootPassword ?? await Provisioner.EnsureRootPasswordAsync(ctx, freshlyInitialized);
        var configured = await Provisioner.ConfigureDatabaseAsync(ctx, rootPassword);
        await Provisioner.MigrateAsync(configured.Settings);

        if (options.NoService)
            Log.Info("--no-service given; the sync service was not installed.");
        else
            Provisioner.RegisterHoardService(ctx, startIt: configured.ServiceCanRunAsLocalSystem);

        // Order matters: the broad rule is applied to the whole tree first, then the two
        // sensitive parts are tightened on top of it.
        Acl.SecureDataRoot(ctx.DataRoot);
        Acl.RestrictTreeToAdministrators(ctx.MySqlConfigDir);
        if (File.Exists(ctx.AdminPasswordFile))
            Acl.RestrictToAdministrators(ctx.AdminPasswordFile);

        Log.Info("Hoard is installed and ready.");
    }

    /// <summary>hoard.json points at the bundled server when it names the loopback address and the bundled port.</summary>
    private static bool IsBundledInstance(DatabaseSettings db, int bundledPort) =>
        db.Port == bundledPort &&
        (string.Equals(db.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(db.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(db.Host, "::1", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Install for a machine whose hoard.json already names a MySQL server that is not the bundled one. The
    /// bundled server is left uninitialized and unregistered (nothing to stop, nothing using memory), the settings
    /// file is untouched, and the sync service is registered in whatever way the protection scope allows. Schema
    /// migrations run here only when the secrets can be decrypted by this process (LocalMachine scope); otherwise
    /// the desktop app applies them the next time it starts, exactly as it always has.
    /// </summary>
    private static async Task InstallAgainstExternalDatabaseAsync(SetupContext ctx, HoardSettings settings, Options options, bool syncWasRunning)
    {
        var db = settings.Database;
        var machineScope = string.Equals(settings.Security.ProtectionScope, "LocalMachine", StringComparison.OrdinalIgnoreCase);
        Log.Info($"hoard.json already points at MySQL {db.Host}:{db.Port} (database '{db.DatabaseName}'), which is not the " +
                 $"bundled server on port {ctx.Port}. Keeping that database; the bundled MySQL will not be initialized or started.");

        if (ServiceManager.Exists(SetupContext.MySqlServiceName))
            Log.Info($"A '{SetupContext.MySqlServiceName}' service from an earlier install exists; leaving it as it is.");

        if (machineScope)
        {
            try
            {
                await Provisioner.MigrateAsync(settings);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not apply schema migrations to the configured database now; the desktop app will apply " +
                         "them when it next starts. " + ex.Message);
            }
        }
        else
        {
            Log.Info("Secrets are sealed to a Windows account (CurrentUser scope), so this installer cannot open the " +
                     "database; the desktop app applies any new schema migrations when it next starts.");
        }

        if (options.NoService)
            Log.Info("--no-service given; the sync service was not installed.");
        else if (machineScope)
            Provisioner.RegisterHoardService(ctx, startIt: true, dependsOnBundledMySql: false);
        else
            Provisioner.RegisterHoardServiceForUserScope(ctx, syncWasRunning);

        Acl.SecureDataRoot(ctx.DataRoot);
        Log.Info("Hoard is installed and is using your existing database.");
    }

    private static void Uninstall(Options options)
    {
        var ctx = SetupContext.Create(ReadConfiguredPort() ?? SetupContext.DefaultPort, options.Purge);

        ServiceManager.StopIfRunning(SetupContext.HoardServiceName, TimeSpan.FromSeconds(60));
        ServiceManager.Delete(SetupContext.HoardServiceName);

        ServiceManager.StopIfRunning(SetupContext.MySqlServiceName, TimeSpan.FromSeconds(120));
        ServiceManager.Delete(SetupContext.MySqlServiceName);

        if (!options.Purge)
        {
            Log.Info($"Your database and settings were left in place under {ctx.DataRoot}. " +
                     "Delete that folder by hand to remove them.");
            return;
        }

        Log.Warn($"--purge given: deleting {ctx.DataRoot}.");
        TryDelete(ctx.MySqlDataDir);
        TryDelete(ctx.MySqlConfigDir);
        TryDeleteFile(SettingsStore.DefaultPath);
        TryDeleteFile(ctx.AdminPasswordFile);
    }

    private static void StopServices()
    {
        ServiceManager.StopIfRunning(SetupContext.HoardServiceName, TimeSpan.FromSeconds(60));
        ServiceManager.StopIfRunning(SetupContext.MySqlServiceName, TimeSpan.FromSeconds(120));
    }

    private static void Status()
    {
        var ctx = SetupContext.Create(ReadConfiguredPort() ?? SetupContext.DefaultPort);
        Log.Info($"Install root:        {ctx.InstallRoot}");
        Log.Info($"Data root:           {ctx.DataRoot}");
        Log.Info($"MySQL port:          {ctx.Port}");
        Log.Info($"MySQL initialized:   {MySqlInstance.IsInitialized(ctx)}");
        Log.Info($"MySQL service:       {(ServiceManager.Exists(SetupContext.MySqlServiceName) ? "installed" : "not installed")}");
        Log.Info($"Sync service:        {(ServiceManager.Exists(SetupContext.HoardServiceName) ? "installed" : "not installed")}");
        var settings = SettingsStore.Load();
        Log.Info($"hoard.json:          {(settings.Database.IsConfigured ? "configured" : "not configured")} " +
                 $"(scope {settings.Security.ProtectionScope}, database {settings.Database.DatabaseName})");
    }

    private static int ShowAdminPassword()
    {
        var ctx = SetupContext.Create(ReadConfiguredPort() ?? SetupContext.DefaultPort);
        var password = Provisioner.ReadAdminPassword(ctx);
        if (password is null)
        {
            Log.Error("No sealed MySQL administrator password is stored on this machine.");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"MySQL administrator login for Hoard's private server (127.0.0.1:{ctx.Port}):");
        Console.WriteLine($"  user:     root");
        Console.WriteLine($"  password: {password}");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Reads the port back out of an existing my.ini so a reinstall or upgrade never moves a server
    /// the user has already pointed hoard.json at.
    /// </summary>
    private static int? ReadConfiguredPort()
    {
        try
        {
            var ini = Path.Combine(SettingsStore.DefaultDirectory, "mysql", "my.ini");
            if (!File.Exists(ini)) return null;
            foreach (var line in File.ReadLines(ini))
            {
                var m = Regex.Match(line, @"^\s*port\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    return p;
            }
        }
        catch
        {
            // Fall back to the default port.
        }
        return null;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {directory}: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options.Port = port;
                    i++;
                    break;
                case "--root-password" when i + 1 < args.Length:
                    options.RootPassword = args[i + 1];
                    i++;
                    break;
                case "--purge":
                    options.Purge = true;
                    break;
                case "--no-service":
                    options.NoService = true;
                    break;
            }
        }
        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            HoardSetup — installs Hoard's private MySQL server, database and Windows services.
            Run from an elevated prompt.

              HoardSetup.exe install [--port 3307] [--root-password <pwd>] [--no-service]
              HoardSetup.exe uninstall [--purge]     --purge also deletes the database and settings
              HoardSetup.exe stop
              HoardSetup.exe status
              HoardSetup.exe show-admin-password

            Logs are written to %ProgramData%\Hoard\logs.
            """);
    }

    private sealed class Options
    {
        public int? Port { get; set; }
        public string? RootPassword { get; set; }
        public bool Purge { get; set; }
        public bool NoService { get; set; }
    }
}
