using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
#if ANDROID
using Android.Content;
using AndroidX.Core.Content;
using Microsoft.Maui.ApplicationModel;
#endif

namespace IClassMobile.Services;

public sealed class AppUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Mycroftxrg/IClassMobile/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler());

    public AppUpdateService()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IClassMobile", CurrentVersionText));
    }

    public static string CurrentVersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
        AppInfo.Current.VersionString ??
        "1.0.0";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApi, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"检查更新失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var release = JsonSerializer.Deserialize<GitHubRelease>(raw, JsonOptions)
            ?? throw new InvalidOperationException("GitHub Releases 返回格式无效。");
        var latestVersionText = release.TagName.Trim().TrimStart('v', 'V');
        var apk = release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
            asset.BrowserDownloadUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null)
        {
            throw new InvalidOperationException("最新版本没有可下载的 APK 安装包。");
        }

        var current = ParseVersion(CurrentVersionText);
        var latest = ParseVersion(latestVersionText);
        return new UpdateCheckResult(
            IsUpdateAvailable: latest > current,
            CurrentVersion: CurrentVersionText,
            LatestVersion: latestVersionText,
            DownloadUrl: apk.BrowserDownloadUrl,
            ReleaseNotes: release.Body?.Trim() ?? string.Empty);
    }

    public async Task<string> DownloadApkAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载更新失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var fileName = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"IClassMobile-{update.LatestVersion}-android.apk";
        }

        var dir = Path.Combine(FileSystem.Current.CacheDirectory, "updates");
        Directory.CreateDirectory(dir);
        var targetPath = Path.Combine(dir, fileName);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(targetPath);
        var buffer = new byte[128 * 1024];
        var total = response.Content.Headers.ContentLength;
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (total is > 0)
            {
                progress?.Report((double)copied / total.Value);
            }
        }

        progress?.Report(1);
        return targetPath;
    }

    public void LaunchInstaller(string apkPath)
    {
#if ANDROID
        var activity = Platform.CurrentActivity ??
                       throw new InvalidOperationException("找不到当前 Android Activity。");
        var authority = $"{activity.PackageName}.fileprovider";
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, authority, new Java.IO.File(apkPath));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
#else
        Process.Start(new ProcessStartInfo
        {
            FileName = apkPath,
            UseShellExecute = true
        });
#endif
    }

    private static Version ParseVersion(string value)
    {
        var clean = value.Trim().TrimStart('v', 'V');
        var marker = clean.IndexOfAny(['-', '+']);
        if (marker >= 0)
        {
            clean = clean[..marker];
        }

        return Version.TryParse(clean, out var parsed) ? parsed : new Version(0, 0);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")]
        string TagName,
        [property: JsonPropertyName("body")]
        string? Body,
        [property: JsonPropertyName("assets")]
        IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string DownloadUrl,
    string ReleaseNotes);
