using Hoard.Core.Updates;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class UpdateTests
{
    private const string Release = """
        {
          "tag_name": "v3.2.0", "name": "Hoard 3.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/jimquery/Hoard/releases/tag/v3.2.0", "published_at": "2026-11-07T16:32:10Z",
          "body": "## Added\n- Updates",
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://example.test/notes.txt" },
            { "name": "Hoard-3.2.0-x64.msi", "size": 190654806, "digest": "sha256:0ABE1d6e",
              "browser_download_url": "https://github.com/jimquery/Hoard/releases/download/v3.2.0/Hoard-3.2.0-x64.msi" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_FindsTheMsiAndDigest()
    {
        var r = UpdateService.ParseRelease(Release)!;
        Assert.Equal(new Version(3, 2, 0), r.Version);
        Assert.Equal("Hoard-3.2.0-x64.msi", r.MsiName);
        Assert.Equal(190654806, r.MsiSize);
        Assert.Equal("0ABE1d6e", r.Sha256);
        Assert.EndsWith("/v3.2.0/Hoard-3.2.0-x64.msi", r.MsiUrl);
        Assert.Equal(new DateTime(2026, 11, 7, 16, 32, 10, DateTimeKind.Utc), r.PublishedUtc);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("Hoard-3.2.0-x64.msi\", \"size\"", "Hoard-3.2.0-x64.zip\", \"size\"")]
    [InlineData("\"tag_name\": \"v3.2.0\"", "\"tag_name\": \"nightly\"")]
    public void ParseRelease_IgnoresDraftsPrereleasesAndReleasesWithoutAnInstaller(string from, string to) =>
        Assert.Null(UpdateService.ParseRelease(Release.Replace(from, to)));

    [Theory]
    [InlineData("v3.1.0", 3, 1, 0)]
    [InlineData("3.1", 3, 1, 0)]
    [InlineData("V3.1.0.7", 3, 1, 0)]
    [InlineData("v4.0.0-beta.1", 4, 0, 0)]
    [InlineData("4", 4, 0, 0)]
    public void ParseVersion_ReadsTags(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), UpdateService.ParseVersion(tag));

    [Fact]
    public void IsNewer_ComparesThreeParts()
    {
        Assert.True(UpdateService.IsNewer(new Version(3, 1, 1), new Version(3, 1, 0)));
        Assert.True(UpdateService.IsNewer(new Version(3, 10, 0), new Version(3, 9, 9)));
        Assert.False(UpdateService.IsNewer(new Version(3, 1, 0), new Version(3, 1, 0, 0)));
        Assert.False(UpdateService.IsNewer(new Version(3, 0, 2), new Version(3, 1, 0)));
    }
}
