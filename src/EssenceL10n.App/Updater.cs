using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace EssenceL10n.App;

public enum UpdateChannel
{
    /// <summary>Velopack 安装版：可原地增量自更新。</summary>
    Velopack,
    /// <summary>Velopack 便携版：可原地自更新。</summary>
    Portable,
    /// <summary>普通文件夹（dotnet publish 输出 / 手动解压）：只能查版本并跳转下载。</summary>
    GitHub,
}

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string? LatestVersion,
    string? CurrentVersion,
    UpdateChannel Channel,
    string? DownloadUrl,
    string? ReleaseUrl,
    string? Error)
{
    public static UpdateCheckResult Fail(string error, UpdateChannel channel, string? current = null) =>
        new(false, null, current, channel, null, null, error);
}

/// <summary>
/// 更新检查。三层：
/// 1) Velopack 安装版 → 走 UpdateManager，可自更新
/// 2) Velopack 便携版 → 走 UpdateManager，可自更新
/// 3) 其它（publish 目录、手动解压的 zip）→ GitHub Releases API，只提示 + 跳转下载
///
/// Velopack 的 UpdateManager 在「未安装」状态下会抛
/// "Cannot perform this operation in an application which is not installed"，
/// 所以必须先判断 IsInstalled / IsPortable，不能直接调 CheckForUpdatesAsync。
/// </summary>
public static class Updater
{
    private const string DefaultRepo = "StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher";

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string? ConfiguredRepo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "update.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var repo = doc.RootElement.TryGetProperty("githubRepo", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(repo) ? null : repo.Trim();
        }
        catch { return null; }
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var repo = ConfiguredRepo() ?? DefaultRepo;
        var current = CurrentVersion;

        UpdateManager? manager = null;
        try
        {
            manager = new UpdateManager(new GithubSource("https://github.com/" + repo, null, false));
        }
        catch (Exception ex)
        {
            return await GitHubFallbackAsync(repo, current, "Velopack 初始化失败: " + ex.Message, cancellationToken);
        }

        // 关键：非安装态调用 CheckForUpdatesAsync 会抛异常，先分流
        if (manager.IsInstalled)
        {
            try
            {
                var update = await manager.CheckForUpdatesAsync();
                if (update is null) return new(false, null, current, UpdateChannel.Velopack, null, null, null);
                return new(true, update.TargetFullRelease.Version.ToString(), current,
                    UpdateChannel.Velopack, null, null, null);
            }
            catch (Exception ex)
            {
                return await GitHubFallbackAsync(repo, current, "Velopack 检查失败: " + ex.Message, cancellationToken);
            }
        }

        if (manager.IsPortable)
        {
            try
            {
                var update = await manager.CheckForUpdatesAsync();
                if (update is null) return new(false, null, current, UpdateChannel.Portable, null, null, null);
                return new(true, update.TargetFullRelease.Version.ToString(), current, UpdateChannel.Portable,
                    null, null, null);
            }
            catch (Exception ex)
            {
                return await GitHubFallbackAsync(repo, current, "Velopack 便携版检查失败: " + ex.Message, cancellationToken);
            }
        }

        // 普通文件夹：这不是缺陷，是发布形态本身的限制
        return await GitHubFallbackAsync(repo, current, "not-installed", cancellationToken);
    }

    /// <summary>安装版/便携版的原地更新。</summary>
    public static async Task<string?> ApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var repo = ConfiguredRepo() ?? DefaultRepo;
        var manager = new UpdateManager(new GithubSource("https://github.com/" + repo, null, false));
        if (!manager.IsInstalled && !manager.IsPortable)
            return "not-installed";

        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return null;

        await manager.DownloadUpdatesAsync(update, p => progress?.Report(p), false, cancellationToken);
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        return null;
    }

    private static async Task<UpdateCheckResult> GitHubFallbackAsync(
        string repo, string current, string? note, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EssenceL10n/" + current);

            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{repo}/releases/latest", cancellationToken);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Fail($"GitHub API {response.StatusCode}", UpdateChannel.GitHub, current);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = tag.TrimStart('v', 'V');
            var releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                // 普通文件夹场景优先给便携 zip，其次安装包
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
                if (downloadUrl is null)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }

            var hasUpdate = CompareVersions(latest, current) > 0;
            return new(hasUpdate, latest, current, UpdateChannel.GitHub, downloadUrl, releaseUrl,
                note == "not-installed" ? null : note);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail("GitHub 检查失败: " + ex.Message, UpdateChannel.GitHub, current);
        }
    }

    /// <summary>语义化版本比较：latest &gt; current 时返回正数。</summary>
    public static int CompareVersions(string latest, string current)
    {
        static int[] Parts(string value)
        {
            var cleaned = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            var pieces = cleaned.Split('.');
            var result = new int[3];
            for (int i = 0; i < 3 && i < pieces.Length; i++)
                int.TryParse(pieces[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]);
            return result;
        }

        var a = Parts(latest);
        var b = Parts(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] - b[i];
        }
        return 0;
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 打不开浏览器就算了 */ }
    }
}
