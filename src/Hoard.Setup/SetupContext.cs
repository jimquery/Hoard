using Hoard.Core.Configuration;

namespace Hoard.Setup;

/// <summary>
/// Every path and name the installer works with, derived from where HoardSetup.exe itself lives.
/// The MSI custom actions call this program with no arguments other than a verb, precisely so that
/// nothing has to be marshalled from Windows Installer properties into a deferred custom action
/// (a deferred action cannot read session properties, and a Type 18 command line with
/// <c>[INSTALLFOLDER]</c> in it would silently format to an empty string).
/// </summary>
public sealed class SetupContext
{
    public const string MySqlServiceName = "HoardMySQL";
    public const string MySqlServiceDisplayName = "Hoard MySQL database";
    public const string HoardServiceName = "Hoard";
    public const string HoardServiceDisplayName = "Hoard financial sync";
    public const string EventLogSource = "Hoard";

    /// <summary>Private port so a MySQL the user already runs on 3306 is never touched.</summary>
    public const int DefaultPort = 3307;

    private SetupContext(string installRoot, int port, bool purge)
    {
        InstallRoot = installRoot;
        Port = port;
        Purge = purge;
    }

    /// <summary>e.g. C:\Program Files\Hoard</summary>
    public string InstallRoot { get; }
    public int Port { get; }
    public bool Purge { get; }

    public string MySqlBaseDir => Path.Combine(InstallRoot, "mysql");
    public string MySqlDaemon => Path.Combine(MySqlBaseDir, "bin", "mysqld.exe");
    public string ServiceExe => Path.Combine(InstallRoot, "Service", "HoardService.exe");
    public string DesktopExe => Path.Combine(InstallRoot, "Desktop", "Hoard.exe");

    /// <summary>C:\ProgramData\Hoard — the same folder SettingsStore uses for hoard.json.</summary>
    public string DataRoot => SettingsStore.DefaultDirectory;

    public string MySqlConfigDir => Path.Combine(DataRoot, "mysql");
    public string MySqlIni => Path.Combine(MySqlConfigDir, "my.ini");
    public string MySqlDataDir => Path.Combine(MySqlConfigDir, "data");
    public string MySqlLogDir => Path.Combine(MySqlConfigDir, "logs");
    public string MySqlErrorLog => Path.Combine(MySqlLogDir, "error.log");
    public string LogDir => Path.Combine(DataRoot, "logs");

    /// <summary>DPAPI-protected (LocalMachine) MySQL root password, ACL'd to Administrators and SYSTEM.</summary>
    public string AdminPasswordFile => Path.Combine(DataRoot, "mysql-admin.dat");

    public static SetupContext Create(int? portOverride = null, bool purge = false)
    {
        // HoardSetup.exe is installed at <InstallRoot>\Setup\HoardSetup.exe.
        var setupDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Path.GetDirectoryName(setupDir)
            ?? throw new InvalidOperationException($"Cannot determine the Hoard install root from '{setupDir}'.");
        return new SetupContext(installRoot, portOverride ?? DefaultPort, purge);
    }
}
