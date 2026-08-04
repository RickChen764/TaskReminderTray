using TaskReminderTray.Services;
using Xunit;

namespace TaskReminderTray.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3.0", 1, 2, 3)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    public void TryParseVersion_AcceptsReleaseTags(
        string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build),
            new Version(version.Major, version.Minor, version.Build));
    }

    [Fact]
    public void ParseChecksum_AcceptsStandardSha256File()
    {
        var hash = new string('a', 64);

        var parsed = UpdateService.ParseChecksum(
            $"{hash}  {UpdateService.ExecutableAssetName}\n");

        Assert.Equal(hash.ToUpperInvariant(), parsed);
    }

    [Fact]
    public void ParseRelease_RequiresExpectedAssets()
    {
        const string json = """
        {
          "tag_name": "v1.1.0",
          "name": "TaskReminderTray v1.1.0",
          "body": "更新说明",
          "html_url": "https://github.com/RickChen764/TaskReminderTray/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "TaskReminderTray-win-x64.exe",
              "size": 71600000,
              "browser_download_url": "https://github.com/RickChen764/TaskReminderTray/releases/download/v1.1.0/TaskReminderTray-win-x64.exe"
            },
            {
              "name": "TaskReminderTray-win-x64.exe.sha256",
              "browser_download_url": "https://github.com/RickChen764/TaskReminderTray/releases/download/v1.1.0/TaskReminderTray-win-x64.exe.sha256"
            }
          ]
        }
        """;

        var release = UpdateService.ParseRelease(json);

        Assert.Equal(new Version(1, 1, 0), release.Version);
        Assert.Equal(UpdateService.ExecutableAssetName,
            Path.GetFileName(release.ExecutableUrl.AbsolutePath));
        Assert.Equal(UpdateService.ChecksumAssetName,
            Path.GetFileName(release.ChecksumUrl.AbsolutePath));
        Assert.Equal(71_600_000, release.ExecutableSize);
    }

    [Fact]
    public void VersionComparison_IgnoresMissingRevision()
    {
        Assert.True(UpdateService.VersionsEquivalent(
            new Version(1, 0, 0), new Version(1, 0, 0, 0)));
        Assert.True(UpdateService.IsNewerVersion(
            new Version(1, 0, 1), new Version(1, 0, 0)));
    }
}
