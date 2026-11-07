using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Updates;

namespace Hoard.Desktop.Services;

/// <summary>
/// Checks for, downloads and installs new Hoard releases. Installing runs the release's MSI through a small hidden
/// PowerShell helper: it waits for Hoard to exit, runs msiexec elevated (Windows asks for consent), and starts Hoard
/// again. The outcome is written next to the MSI and reported on the next start.
/// </summary>
public sealed partial class AppUpdater : ObservableObject
{
    private readonly HoardSettings _settings;
    private readonly UpdateService _updates;

    public AppUpdater(HoardSettings settings, UpdateService updates)
    {
        _settings = settings;
        _updates = updates;
        _autoCheck = settings.Updates.AutoCheck;
        _autoInstall = settings.Updates.AutoInstall;
        _status = LastCheckText();
    }

    public string CurrentVersionText => $"Hoard {UpdateService.CurrentVersion.ToString(3)}";

    [ObservableProperty] private bool _autoCheck;
    [ObservableProperty] private bool _autoInstall;
    [ObservableProperty] private string _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(AvailableText))]
    private ReleaseInfo? _available;

    public bool HasUpdate => Available is not null;
    public string AvailableText => Available is { } r ? $"{r.Name} is available{(r.PublishedUtc is { } p ? $" (released {p.ToLocalTime():MMM d})" : "")}." : "";

    /// <summary>The folder the MSI installed Hoard into, or null when this copy was not installed by it (a dev build).</summary>
    public static string? InstallFolder
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            var root = exeDir is null ? null : Path.GetDirectoryName(exeDir);
            return root is not null && File.Exists(Path.Combine(root, "Setup", "HoardSetup.exe")) ? root : null;
        }
    }

    public bool CanInstall => InstallFolder is not null;

    partial void OnAutoCheckChanged(bool value)
    {
        _settings.Updates.AutoCheck = value;
        if (!value) AutoInstall = false;
        SettingsStore.Save(_settings);
        Status = value ? "Hoard checks for new releases soon after it starts and once a day." : "Automatic update checks are off.";
    }

    partial void OnAutoInstallChanged(bool value)
    {
        _settings.Updates.AutoInstall = value;
        if (value && !AutoCheck) AutoCheck = true;
        if (value) _settings.Updates.SkippedVersion = null;
        SettingsStore.Save(_settings);
    }

    /// <summary>Reports how the last install went, once, on the first start after it. Returns a status line or null.</summary>
    public string? ReportPreviousInstall()
    {
        var u = _settings.Updates;
        if (u.InstallingVersion is not { } target) return null;
        u.InstallingVersion = null;
        var code = ReadResult(target);
        string message;
        if (UpdateService.ParseVersion(target) is { } v && !UpdateService.IsNewer(v, UpdateService.CurrentVersion))
        {
            message = $"Updated to Hoard {target}.";
            u.SkippedVersion = null;
        }
        else
        {
            // Not retried automatically, so a cancelled consent prompt or a failing installer does not come back every day.
            u.SkippedVersion = target;
            message = code is 1602 or 1223
                ? $"The update to Hoard {target} was cancelled. Install it from Settings › Updates when you're ready."
                : $"The update to Hoard {target} did not complete{(code is { } c ? $" (installer exit code {c})" : "")}. Log: {LogPath(target)}";
        }
        SettingsStore.Save(_settings);
        Status = message;
        return message;
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Available = await _updates.CheckAsync();
            Status = Available is null ? $"You're up to date ({CurrentVersionText}). Checked {DateTime.Now:h:mm tt}." : AvailableText;
        }
        catch (Exception ex) { Status = "Could not check for updates: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallNowAsync()
    {
        if (Available is not { } release || IsBusy) return;
        if (!CanInstall) { Status = "This copy of Hoard was not installed by the MSI, so it cannot update itself."; return; }
        await DownloadAndInstallAsync(release);
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = Available?.HtmlUrl ?? $"https://github.com/{UpdateService.Repository}/releases/latest";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Called by the scheduler every minute: checks when a check is due and, with automatic install on, installs a new
    /// release unless the app is busy. Returns a status line when something happened.
    /// </summary>
    public async Task<string?> RunScheduledAsync(bool appBusy)
    {
        if (IsBusy) return null;
        if (_updates.IsCheckDue(DateTime.UtcNow))
        {
            IsBusy = true;
            try { Available = await _updates.CheckAsync(); }
            catch (Exception ex)
            {
                Status = "Could not check for updates: " + ex.Message;
                ErrorLog.Write(ex, "Update check");
                return null;
            }
            finally { IsBusy = false; }
            Status = Available is null ? LastCheckText() : AvailableText;
        }

        if (Available is not { } release || !AutoInstall || !CanInstall || appBusy) return null;
        if (_settings.Updates.SkippedVersion == release.Version.ToString(3)) return null;
        return await DownloadAndInstallAsync(release) ? null : Status;
    }

    private async Task<bool> DownloadAndInstallAsync(ReleaseInfo release)
    {
        IsBusy = true;
        Progress = 0;
        var version = release.Version.ToString(3);
        try
        {
            Status = $"Downloading Hoard {version}…";
            var progress = new Progress<double>(p => { Progress = p * 100; Status = $"Downloading Hoard {version}… {p:P0}"; });
            var msi = await _updates.DownloadAsync(release, progress);

            Status = $"Installing Hoard {version}. Hoard will close, Windows will ask to allow the installer, and Hoard reopens when it finishes.";
            _settings.Updates.InstallingVersion = version;
            SettingsStore.Save(_settings);
            StartInstaller(msi, version);
            await Task.Delay(1500); // let the message show before the window goes
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            _settings.Updates.InstallingVersion = null;
            _settings.Updates.SkippedVersion = version; // a failing download is not retried every minute
            SettingsStore.Save(_settings);
            Status = $"Could not install Hoard {version}: {ex.Message}";
            ErrorLog.Write(ex, "Update install");
            return false;
        }
        finally { IsBusy = false; }
    }

    private static string LogPath(string version) => Path.Combine(UpdateService.DownloadFolder, $"install-{version}.log");

    private static int? ReadResult(string version)
    {
        try { return int.TryParse(File.ReadAllText(LogPath(version) + ".result").Trim(), out var code) ? code : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void StartInstaller(string msi, string version)
    {
        var folder = InstallFolder ?? throw new InvalidOperationException("Hoard's install folder was not found.");
        var exe = Environment.ProcessPath ?? Path.Combine(folder, "Desktop", "Hoard.exe");
        var script = Path.Combine(UpdateService.DownloadFolder, "install-update.ps1");
        File.WriteAllText(script, HelperScript);
        var log = LogPath(version);
        try { File.Delete(log + ".result"); } catch (IOException) { }

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-WaitPid", Environment.ProcessId.ToString(), "-Msi", msi, "-InstallFolder", folder.TrimEnd('\\'), "-Log", log, "-Exe", exe,
                 })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    private string LastCheckText()
    {
        var u = _settings.Updates;
        if (u.LastCheckError is { } error) return "Last update check failed: " + error;
        if (u.LastCheckUtc is not { } last) return u.AutoCheck ? "Hoard checks for new releases at start-up and once a day." : "Automatic update checks are off.";
        return $"Last checked {last.ToLocalTime():ddd MMM d, h:mm tt}" + (u.LatestVersion is { } v ? $" · latest release {v}." : ".");
    }

    // Waits for Hoard to exit, runs the MSI elevated with the same install folder, records the exit code, restarts Hoard.
    private const string HelperScript = """
        param([int]$WaitPid, [string]$Msi, [string]$InstallFolder, [string]$Log, [string]$Exe)
        try { Wait-Process -Id $WaitPid -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        $msiArgs = @('/i', "`"$Msi`"", '/passive', '/norestart', '/l*v', "`"$Log`"", "INSTALLFOLDER=`"$InstallFolder`"")
        try {
            $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Verb RunAs -Wait -PassThru
            $code = $p.ExitCode
        } catch { $code = 1602 }
        Set-Content -Path "$Log.result" -Value $code
        if (Test-Path $Exe) { Start-Process -FilePath $Exe }
        """;
}
