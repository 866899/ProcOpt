namespace ProcOpt.Models;

public class AppSettings
{
    public bool AutoApply { get; set; } = true;
    public int PollIntervalMs { get; set; } = 2000;
    public bool NotifyOnApply { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>内存占用超阈值时自动清理</summary>
    public bool AutoCleanMemory { get; set; } = false;
    /// <summary>自动清理阈值（物理内存占用 %）</summary>
    public int AutoCleanThresholdPct { get; set; } = 85;
    /// <summary>两次自动清理最小间隔（分钟）</summary>
    public int AutoCleanIntervalMin { get; set; } = 10;

    /// <summary>更新下载渠道索引（0=GitHub直连，1/2=加速代理，见 UpdateService.ProxyUrls）</summary>
    public int UpdateProxyIndex { get; set; } = 0;
}
