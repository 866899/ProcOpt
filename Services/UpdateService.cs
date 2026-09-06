using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ProcOpt.Services;

/// <summary>GitHub Releases 在线更新（仓库仅存编译产物，不含源码）</summary>
public static class UpdateService
{
    // 发布渠道：GitHub 公开仓库 Releases
    public const string RepoOwner = "866899";
    public const string RepoName = "ProcOpt";

    /// <summary>更新加速代理前缀（拼接方式：{前缀}https://github.com/...）。空串 = 直连</summary>
    public static readonly string[] ProxyUrls = { "", "https://gh-proxy.org/", "https://ghfast.top/" };
    /// <summary>设置页显示的渠道名称</summary>
    public static readonly string[] ProxyNames = { "GitHub 直连", "gh-proxy.org（国内推荐）", "ghfast.top（国内推荐）" };

    /// <summary>当前选择的代理前缀</summary>
    private static string ProxyPrefix
    {
        get
        {
            var s = App.SettingsSvc?.Settings;
            return s != null && s.UpdateProxyIndex > 0 && s.UpdateProxyIndex < ProxyUrls.Length
                ? ProxyUrls[s.UpdateProxyIndex]
                : "";
        }
    }

    /// <summary>给 GitHub 原始 URL 套上当前选择的代理前缀</summary>
    private static string Via(string url) => ProxyPrefix + url;

    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient HttpNoRedirect = CreateClient(noRedirect: true);

    private static HttpClient CreateClient(bool noRedirect = false)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = !noRedirect };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ProcOpt-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Release 信息摘要</summary>
    public class ReleaseInfo
    {
        public string Tag { get; set; }             // 如 v1.2.0
        public string Name { get; set; }            // Release 标题
        public string Notes { get; set; }           // 更新说明
        public string AssetUrl { get; set; }        // ProcOpt.exe 直链
        public long AssetSize { get; set; }         // 字节
        public DateTime PublishedAt { get; set; }
        public Version Version => ParseVersion(Tag);
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static Version ParseVersion(string tag)
    {
        var s = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>查询最新 Release。无 Release 或网络失败返回 null（errMsg 给出原因）。</summary>
    public static async Task<(ReleaseInfo info, string error)> CheckAsync()
    {
        try
        {
            // 通道 1：releases/latest 网页 302 重定向（普通网页请求，不受 GitHub API 限流影响）
            // 404（无任何发布）→ tag=null 且无异常；网络/代理拒绝 → 抛异常记入 webErr
            string tag = null, webErr = null;
            try { tag = await GetLatestTagViaWebAsync(); }
            catch (Exception ex) { webErr = ex.Message; }

            if (tag == null && webErr == null)
                return (null, "服务器上暂无任何发布版本");

            ReleaseInfo info;
            if (tag != null)
            {
                info = new ReleaseInfo
                {
                    Tag = tag,
                    Name = $"ProcOpt {tag}",
                    AssetUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/ProcOpt.exe"
                };

                // 通道 2（补充）：API 获取更新说明 / 文件大小（被限流或代理不支持时静默降级）
                var api = await FetchFromApiAsync();
                if (api != null)
                {
                    info.Name = api.Name;
                    info.Notes = api.Notes;
                    info.PublishedAt = api.PublishedAt;
                    info.AssetUrl = api.AssetUrl;
                    info.AssetSize = api.AssetSize;
                }
            }
            else
            {
                // 网页通道不可用（gh-proxy.org 拒绝网页请求、仅支持 API/文件下载）时，API 通道兜底
                info = await FetchFromApiAsync();
                if (info == null)
                    return (null, $"网络错误：{webErr}");
            }

            if (info.Version == new Version(0, 0, 0))
                return (null, $"无法解析版本号：{info.Tag}");
            return (info, null);
        }
        catch (Exception ex)
        {
            return (null, $"网络错误：{ex.Message}");
        }
    }

    /// <summary>通过 releases/latest 的 302 Location 提取最新 tag（如 v1.1.0）。404（无 Release）返回 null。</summary>
    private static async Task<string> GetLatestTagViaWebAsync()
    {
        using var resp = await HttpNoRedirect.GetAsync(
            Via($"https://github.com/{RepoOwner}/{RepoName}/releases/latest"));
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        // 预期就是 302/301 重定向（禁用了自动跳转），Location 即最新 tag 页面；不能调 EnsureSuccessStatusCode
        var loc = resp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc))
            throw new HttpRequestException($"服务器返回意外状态：{(int)resp.StatusCode} {resp.StatusCode}");

        // Location 形如 .../releases/tag/v1.1.0
        var idx = loc.IndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : loc[(idx + 5)..].TrimEnd('/');
    }

    /// <summary>经 GitHub API 获取完整 Release 信息（tag/标题/说明/资产直链/大小）。
    /// 任何失败（网络、403 限流、代理不支持 API）返回 null。</summary>
    private static async Task<ReleaseInfo> FetchFromApiAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(
                Via($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest"));
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag)) return null;

            var info = new ReleaseInfo
            {
                Tag = tag,
                Name = root.GetProperty("name").GetString() ?? $"ProcOpt {tag}",
                Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                PublishedAt = root.TryGetProperty("published_at", out var pub) &&
                              DateTime.TryParse(pub.GetString(), out var dt) ? dt : DateTime.MinValue,
                AssetUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/ProcOpt.exe"
            };

            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.Equals("ProcOpt.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.AssetUrl = a.GetProperty("browser_download_url").GetString();
                        info.AssetSize = a.GetProperty("size").GetInt64();
                        break;
                    }
                }
            return info;
        }
        catch { return null; /* 被限流或解析失败：由调用方降级处理 */ }
    }

    /// <summary>是否有新版本</summary>
    public static bool HasNewer(ReleaseInfo info) => info != null && info.Version > CurrentVersion;

    /// <summary>下载新版本到同目录 ProcOpt.exe.new，report 收到 0~1 进度。成功返回本地路径。</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo info, IProgress<double> report, CancellationToken ct = default)
    {
        if (info?.AssetUrl == null) throw new InvalidOperationException("该版本没有可下载的文件");

        var target = Path.Combine(AppContext.BaseDirectory, "ProcOpt.exe.new");
        // AssetUrl 始终是 github.com 原始直链，此处按当前渠道套上代理前缀
        using var resp = await Http.GetAsync(Via(info.AssetUrl), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        long total = info.AssetSize > 0 ? info.AssetSize : resp.Content.Headers.ContentLength ?? 0;
        var buf = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) report?.Report((double)read / total);
        }
        return target;
    }

    /// <summary>应用更新：生成替换脚本 → 等当前进程退出 → 换文件 → 重启。调用后应立即退出程序。</summary>
    public static void ApplyAndRestart()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ProcOpt.exe");
        string dir = AppContext.BaseDirectory;
        string newFile = Path.Combine(dir, "ProcOpt.exe.new");
        if (!File.Exists(newFile))
            throw new FileNotFoundException("更新包不存在，请重新下载", newFile);

        string bat = Path.Combine(Path.GetTempPath(), "ProcOpt_update.bat");
        int oldPid = Environment.ProcessId;

        // 关键：必须等旧进程 PID 消失后再替换并重启。
        // Windows 允许重命名正在运行的 exe，旧脚本靠 ren 失败判断退出根本等不到，
        // 导致新实例过早启动、撞上旧进程仍持有的单实例互斥锁而自行退出。
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "setlocal\r\n" +
            $"set \"EXE={exe}\"\r\n" +
            $"set \"NEW={newFile}\"\r\n" +
            $"set \"OLDPID={oldPid}\"\r\n" +
            "set /a w=0\r\n" +
            ":waitproc\r\n" +
            "tasklist /FI \"PID eq %OLDPID%\" 2>nul | find /I \"%OLDPID%\" >nul\r\n" +
            "if errorlevel 1 goto renloop\r\n" +
            "set /a w+=1\r\n" +
            "if %w% geq 20 goto renloop\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            "goto waitproc\r\n" +
            ":renloop\r\n" +
            "if exist \"%EXE%.old\" del /f /q \"%EXE%.old\" >nul 2>&1\r\n" +
            "if not exist \"%NEW%\" exit /b 1\r\n" +
            "set /a r=0\r\n" +
            ":rentry\r\n" +
            "set /a r+=1\r\n" +
            "if %r% gtr 10 exit /b 1\r\n" +
            "ren \"%EXE%\" ProcOpt.exe.old >nul 2>&1\r\n" +
            "if exist \"%EXE%.old\" goto renamed\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            "goto rentry\r\n" +
            ":renamed\r\n" +
            "ren \"%NEW%\" ProcOpt.exe >nul 2>&1\r\n" +
            "if not exist \"%EXE%\" (\r\n" +
            "  ren \"%EXE%.old\" ProcOpt.exe >nul 2>&1\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            "start \"\" \"%EXE%\"\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "del /f /q \"%EXE%.old\" >nul 2>&1\r\n" +
            "del /f /q \"%~f0\" >nul 2>&1\r\n" +
            "endlocal\r\n");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
