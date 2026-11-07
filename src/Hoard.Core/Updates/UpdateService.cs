using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Hoard.Core.Configuration;

namespace Hoard.Core.Updates;

/// <summary>A published Hoard release with an installer attached.</summary>
public sealed record ReleaseInfo(
    Version Version, string Tag, string Name, string Notes, string HtmlUrl, DateTime? PublishedUtc,
    string MsiName, string MsiUrl, long MsiSize, string? Sha256);

/// <summary>
/// Finds new Hoard releases on GitHub and downloads their MSI. Installing is left to the desktop app, which has to
/// exit first so the installer can replace its files.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "jimquery/Hoard";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly HoardSettings _settings;

    public UpdateService(HttpClient http, HoardSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>The running version (the assembly version, x.y.z).</summary>
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Where downloaded installers are kept (per user, so no elevation is needed to write them).</summary>
    public static string DownloadFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hoard", "Updates");

    /// <summary>The latest release if it is newer than <paramref name="current"/> and has an MSI, else null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(Version? current = null, CancellationToken ct = default)
    {
        var updates = _settings.Updates;
        updates.LastCheckUtc = DateTime.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { updates.LastCheckError = null; return null; } // no releases yet
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var release = ParseRelease(await response.Content.ReadAsStringAsync(ct));
            updates.LastCheckError = null;
            updates.LatestVersion = release?.Version.ToString(3);
            return release is not null && IsNewer(release.Version, current ?? CurrentVersion) ? release : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            updates.LastCheckError = ex is TaskCanceledException ? "GitHub did not answer in time." : ex.Message;
            throw;
        }
        finally { SettingsStore.Save(_settings); }
    }

    /// <summary>Whether a scheduled check is due: automatic checks on, and none in the last day.</summary>
    public bool IsCheckDue(DateTime utcNow) =>
        _settings.Updates.AutoCheck && (_settings.Updates.LastCheckUtc is not { } last || utcNow - last >= CheckInterval || last > utcNow);

    /// <summary>
    /// Downloads the release's MSI (or reuses an earlier complete download) and returns its path. The file is checked
    /// against the size and, when GitHub publishes one, the SHA-256 digest.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, Path.GetFileName(release.MsiName));
        if (File.Exists(path) && await IsIntactAsync(path, release, ct)) { progress?.Report(1); return path; }

        var partial = path + ".part";
        using (var response = await _http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.MsiSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (!await IsIntactAsync(partial, release, ct))
        {
            File.Delete(partial);
            throw new InvalidDataException($"The downloaded {release.MsiName} does not match the release (size or SHA-256 differ). Try again.");
        }
        File.Move(partial, path, overwrite: true);
        RemoveOtherDownloads(path);
        return path;
    }

    private static async Task<bool> IsIntactAsync(string path, ReleaseInfo release, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (release.MsiSize > 0 && info.Length != release.MsiSize) return false;
        if (release.Sha256 is null) return true;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveOtherDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(DownloadFolder, "*.msi*"))
        {
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads a GitHub "release" JSON document; null for drafts, pre-releases and releases without an x64 MSI.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name") ?? "";
        if (ParseVersion(tag) is not { } version) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        JsonElement? msi = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name") ?? "";
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            if (msi is null || name.Contains("x64", StringComparison.OrdinalIgnoreCase)) msi = asset;
        }
        if (msi is not { } m || Str(m, "browser_download_url") is not { } url) return null;

        var digest = Str(m, "digest");
        var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
        DateTime? published = DateTime.TryParse(Str(root, "published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        return new ReleaseInfo(
            version, tag, Str(root, "name") is { Length: > 0 } n ? n : $"Hoard {version.ToString(3)}", Str(root, "body") ?? "",
            Str(root, "html_url") ?? $"https://github.com/{Repository}/releases/tag/{tag}", published,
            Str(m, "name")!, url, m.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : 0, sha);
    }

    /// <summary>"v3.1.0" / "3.1" / "3.1.0.0" → 3.1.0; null when the tag is not a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(text)) return null;
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text.Contains('.') ? text : text + ".0", out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
