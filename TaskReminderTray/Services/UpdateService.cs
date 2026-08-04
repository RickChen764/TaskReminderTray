using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TaskReminderTray.Models;

namespace TaskReminderTray.Services;

internal sealed class UpdateService : IDisposable
{
    internal const string RepositoryOwner = "RickChen764";
    internal const string RepositoryName = "TaskReminderTray";
    internal const string ExecutableAssetName = "TaskReminderTray-win-x64.exe";
    internal const string ChecksumAssetName = "TaskReminderTray-win-x64.exe.sha256";
    private const long MaximumExecutableBytes = 300L * 1024 * 1024;

    private readonly HttpClient _httpClient;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TaskReminderTray", CurrentVersion.ToString(3)));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    public static Uri ReleasesPage =>
        new($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri(
            $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return await CheckFromPublicFeedAsync(cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        var release = ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken));
        return IsNewerVersion(release.Version, CurrentVersion) ? release : null;
    }

    private async Task<UpdateRelease?> CheckFromPublicFeedAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases.atom");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024)
        {
            throw new UpdateException("GitHub Release Feed 尺寸异常。");
        }

        return ParseReleaseFeed(await response.Content.ReadAsStringAsync(cancellationToken),
            CurrentVersion);
    }

    public async Task<DownloadedUpdate> DownloadAndVerifyAsync(
        UpdateRelease release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDownloadUri(release.ExecutableUrl);
        ValidateDownloadUri(release.ChecksumUrl);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskReminderTray", "updates", release.Tag);
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, ExecutableAssetName);
        var partialPath = executablePath + ".download";
        progress?.Report(new UpdateProgress(UpdateProgressStage.Preparing));
        var expectedHash = await DownloadChecksumAsync(release.ChecksumUrl, cancellationToken);

        try
        {
            using var response = await _httpClient.GetAsync(release.ExecutableUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? release.ExecutableSize;
            if (length is > MaximumExecutableBytes)
            {
                throw new UpdateException("更新包超过允许的最大尺寸。");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partialPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            var lastPercentage = -1;
            progress?.Report(new UpdateProgress(UpdateProgressStage.Downloading,
                length is > 0 ? 0 : null));
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > MaximumExecutableBytes)
                {
                    throw new UpdateException("更新包超过允许的最大尺寸。");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (length is > 0)
                {
                    var percentage = (int)Math.Clamp(received * 100 / length.Value, 0, 100);
                    if (percentage != lastPercentage)
                    {
                        lastPercentage = percentage;
                        progress?.Report(new UpdateProgress(
                            UpdateProgressStage.Downloading, percentage));
                    }
                }
            }

            await output.FlushAsync(cancellationToken);
            output.Close();
            progress?.Report(new UpdateProgress(UpdateProgressStage.Verifying));
            var actualHash = await ComputeSha256Async(partialPath, cancellationToken);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("更新包 SHA-256 校验失败，已取消安装。");
            }

            var fileVersion = FileVersionInfo.GetVersionInfo(partialPath).FileVersion;
            if (!TryParseVersion(fileVersion ?? string.Empty, out var executableVersion) ||
                !VersionsEquivalent(executableVersion, release.Version))
            {
                throw new UpdateException(
                    $"更新包版本与 Release 不一致（文件 {fileVersion ?? "未知"}，Release {release.Tag}）。");
            }

            File.Move(partialPath, executablePath, overwrite: true);
            return new DownloadedUpdate(executablePath, actualHash);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    internal static UpdateRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ??
                  throw new UpdateException("Release 缺少版本标签。");
        if (!TryParseVersion(tag, out var version))
        {
            throw new UpdateException($"无法识别 Release 版本：{tag}");
        }

        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var executable = FindAsset(assets, ExecutableAssetName);
        var checksum = FindAsset(assets, ChecksumAssetName);
        return new UpdateRelease(
            version,
            tag,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            ReadHttpsUri(root.GetProperty("html_url").GetString(), "Release 页面"),
            ReadHttpsUri(executable.GetProperty("browser_download_url").GetString(), "更新包"),
            ReadHttpsUri(checksum.GetProperty("browser_download_url").GetString(), "校验文件"),
            executable.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)
                ? bytes : null);
    }

    internal static UpdateRelease? ParseReleaseFeed(string xml, Version currentVersion)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            foreach (var entry in document.Root?.Elements(atom + "entry") ?? [])
            {
                var pageValue = entry.Elements(atom + "link")
                    .FirstOrDefault(link => (string?)link.Attribute("rel") == "alternate")?
                    .Attribute("href")?.Value;
                if (!Uri.TryCreate(pageValue, UriKind.Absolute, out var pageUrl))
                {
                    continue;
                }

                var tag = pageUrl.Segments.LastOrDefault()?.Trim('/');
                if (!TryParseVersion(tag ?? string.Empty, out var version) ||
                    !IsNewerVersion(version, currentVersion) || (tag?.Contains('-') ?? false))
                {
                    continue;
                }

                var root = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest/download/";
                return new UpdateRelease(version, tag!,
                    entry.Element(atom + "title")?.Value ?? tag!,
                    StripHtml(entry.Element(atom + "content")?.Value), pageUrl,
                    new Uri(root + ExecutableAssetName), new Uri(root + ChecksumAssetName), null);
            }

            return null;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new UpdateException("GitHub Release Feed 格式无效。", exception);
        }
    }

    internal static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var metadata = normalized.IndexOfAny(['-', '+']);
        if (metadata >= 0)
        {
            normalized = normalized[..metadata];
        }
        return Version.TryParse(normalized, out version!);
    }

    internal static bool VersionsEquivalent(Version left, Version right) =>
        NormalizeVersion(left) == NormalizeVersion(right);

    internal static bool IsNewerVersion(Version candidate, Version current) =>
        NormalizeVersion(candidate) > NormalizeVersion(current);

    internal static string ParseChecksum(string content)
    {
        var candidate = content.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (candidate is null || candidate.Length != 64 ||
            candidate.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new UpdateException("SHA-256 校验文件格式无效。");
        }
        return candidate.ToUpperInvariant();
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private async Task<string> DownloadChecksumAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 16 * 1024)
        {
            throw new UpdateException("SHA-256 校验文件尺寸异常。");
        }
        return ParseChecksum(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static JsonElement FindAsset(JsonElement[] assets, string name) =>
        assets.FirstOrDefault(asset => asset.TryGetProperty("name", out var assetName) &&
            string.Equals(assetName.GetString(), name, StringComparison.OrdinalIgnoreCase)) is var found &&
        found.ValueKind != JsonValueKind.Undefined
            ? found
            : throw new UpdateException($"Release 缺少文件：{name}");

    private static Uri ReadHttpsUri(string? value, string fieldName) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new UpdateException($"{fieldName}地址无效。");

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("更新地址不是受信任的 GitHub HTTPS 地址。");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string StripHtml(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record DownloadedUpdate(string ExecutablePath, string Sha256);

internal sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception innerException) : base(message, innerException) { }
}
