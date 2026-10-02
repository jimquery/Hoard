using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Hoard.Setup;

/// <summary>
/// Thin wrapper over sc.exe (create/config/delete/description) and ServiceController (start/stop/query).
/// Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/> so .NET does the quoting;
/// sc.exe still needs the "key= value" form, which is why the "=" and its value are separate entries.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceManager
{
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    public static bool Exists(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static void CreateOrUpdate(
        string name, string displayName, string binPath, string description,
        bool delayedAutoStart, bool manualStart = false, string? dependsOn = null)
    {
        var start = manualStart ? "demand" : "auto";
        if (Exists(name))
        {
            Log.Info($"Service '{name}' already exists; reconfiguring it.");
            Sc("config", name, "binPath=", binPath, "start=", delayedAutoStart ? "delayed-auto" : start,
               "obj=", "LocalSystem", "DisplayName=", displayName);
        }
        else
        {
            Sc("create", name, "binPath=", binPath, "start=", delayedAutoStart ? "delayed-auto" : start,
               "obj=", "LocalSystem", "DisplayName=", displayName);
            Log.Info($"Created service '{name}'.");
        }

        Sc("description", name, description);

        // "depend= X" replaces the whole dependency list, which is what is wanted here.
        if (dependsOn is not null)
            Sc("config", name, "depend=", dependsOn);

        // Restart twice on failure, then give up; keeps a transient MySQL hiccup from stopping the sync.
        Sc("failure", name, "reset=", "86400", "actions=", "restart/60000/restart/120000//0");
    }

    public static bool IsRunning(string name)
    {
        if (!Exists(name)) return false;
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Points an existing service at a new executable and nothing else: log-on account, start type and
    /// dependencies stay as the user configured them. Used when the user runs the sync service under their own
    /// Windows account (CurrentUser protection scope) and an upgrade must not silently switch it to LocalSystem.
    /// </summary>
    public static void SetBinaryPath(string name, string binPath)
    {
        Sc("config", name, "binPath=", binPath);
        Log.Info($"Service '{name}' now runs {binPath}; its log-on account and start type were left as they were.");
    }

    public static void Start(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            Log.Info($"Service '{name}' is already running.");
            return;
        }
        Log.Info($"Starting service '{name}'...");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
        Log.Info($"Service '{name}' is running.");
    }

    public static void StopIfRunning(string name, TimeSpan timeout)
    {
        if (!Exists(name)) return;
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                return;
            }
            Log.Info($"Stopping service '{name}'...");
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            Log.Info($"Service '{name}' stopped.");
        }
        catch (Exception ex)
        {
            // Never let a stubborn service abort an uninstall.
            Log.Warn($"Could not stop service '{name}': {ex.Message}");
        }
    }

    public static void Delete(string name)
    {
        if (!Exists(name)) return;
        var exit = ScAllowing("delete", ErrorServiceDoesNotExist, ErrorServiceMarkedForDelete, name);
        Log.Info(exit == 0
            ? $"Deleted service '{name}'."
            : $"Service '{name}' was already marked for deletion.");
    }

    private static void Sc(params string[] args) => ScAllowing(args[0], -1, -1, args[1..]);

    private static int ScAllowing(string verb, int allowed1, int allowed2, params string[] rest)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(verb);
        foreach (var a in rest) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start sc.exe.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0 && p.ExitCode != allowed1 && p.ExitCode != allowed2)
        {
            throw new InvalidOperationException(
                $"sc.exe {verb} failed with exit code {p.ExitCode}.{Environment.NewLine}{stdout}{stderr}".Trim());
        }
        return p.ExitCode;
    }
}
