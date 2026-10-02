using System.Diagnostics;
using System.Runtime.Versioning;

namespace Hoard.Setup;

/// <summary>
/// Sets permissions on %ProgramData%\Hoard. icacls is used rather than the managed ACL APIs because
/// well-known SIDs sidestep the localized group names ("Administrateurs", "Administratoren", ...)
/// that break name-based rules.
///
/// The shape is deliberate: ordinary users keep write access to the Hoard folder itself, because the
/// desktop app writes hoard.json (theme, sync interval, database settings) while running unelevated —
/// but the MySQL configuration, its data directory and the sealed administrator password are readable
/// only by SYSTEM and Administrators.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Acl
{
    private const string System = "*S-1-5-18";             // NT AUTHORITY\SYSTEM
    private const string Administrators = "*S-1-5-32-544"; // BUILTIN\Administrators
    private const string Users = "*S-1-5-32-545";          // BUILTIN\Users

    /// <summary>SYSTEM and Administrators full control; nobody else. Used for sealed secrets.</summary>
    public static void RestrictToAdministrators(string path) =>
        Run(path, "/inheritance:r", "/grant:r", $"{System}:(F)", "/grant:r", $"{Administrators}:(F)");

    /// <summary>Same, applied to a whole directory tree (the MySQL configuration and data).</summary>
    public static void RestrictTreeToAdministrators(string path)
    {
        if (!Directory.Exists(path)) return;
        Run(path, "/inheritance:r",
            "/grant:r", $"{System}:(OI)(CI)(F)",
            "/grant:r", $"{Administrators}:(OI)(CI)(F)",
            "/T", "/C", "/Q");
    }

    /// <summary>
    /// %ProgramData%\Hoard: full control for SYSTEM and Administrators, modify for ordinary users so
    /// the desktop app can still save its settings without elevation.
    /// </summary>
    public static void SecureDataRoot(string path)
    {
        if (!Directory.Exists(path)) return;
        Run(path, "/inheritance:r",
            "/grant:r", $"{System}:(OI)(CI)(F)",
            "/grant:r", $"{Administrators}:(OI)(CI)(F)",
            "/grant:r", $"{Users}:(OI)(CI)(M)",
            "/T", "/C", "/Q");
    }

    private static void Run(string path, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(path);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                Log.Warn($"icacls on {path} returned {p.ExitCode}: {stdout}{stderr}".Trim());
        }
        catch (Exception ex)
        {
            // A weaker ACL is not worth failing the install for; it is logged instead.
            Log.Warn($"Could not set permissions on {path}: {ex.Message}");
        }
    }
}
