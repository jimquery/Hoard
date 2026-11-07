using System.IO;
using Hoard.Core.Configuration;

namespace Hoard.Desktop.Services;

/// <summary>Appends full exceptions to desktop-error.log next to hoard.json, for errors the UI shows only as one line.</summary>
public static class ErrorLog
{
    public static string Path => System.IO.Path.Combine(SettingsStore.DefaultDirectory, "desktop-error.log");

    /// <summary>Writes the exception; returns the log path, or null when the log could not be written.</summary>
    public static string? Write(Exception ex, string? context = null)
    {
        try
        {
            var header = context is null ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}" : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {context}";
            File.AppendAllText(Path, $"{header}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            return Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
