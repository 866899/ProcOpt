using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ProcOpt.Infrastructure;
using ProcOpt.Services;

namespace ProcOpt.ViewModels;

/// <summary>单个磁盘分区行</summary>
public class DiskCellVm : ObservableObject
{
    public string Letter { get; init; }
    public string Label { get; init; }

    private double _usedPct;
    public double UsedPct { get => _usedPct; private set => SetProperty(ref _usedPct, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public void Update(double usedGb, double totalGb, double usedPct)
    {
        UsedPct = usedPct;
        Summary = $"{usedGb:0.#} / {totalGb:0.#} GB";
    }
}

/// <summary>单个显示器行</summary>
public class MonitorVm
{
    public string Name { get; init; } = "通用即插即用显示器";
    public bool IsPrimary { get; init; }
    /// <summary>右侧摘要：接口 + 尺寸，如 "HDMI · 27""</summary>
    public string TagText { get; init; } = "";
    /// <summary>当前模式：如 "3840×2160 @ 144Hz · 缩放 150%"</summary>
    public string ModeText { get; init; } = "";
}

/// <summary>单个网卡行</summary>
public class NicVm
{
    public string KindText { get; init; } = "";       // "有线" / "无线"
    public Brush KindBrush { get; init; }             // 类型文字色
    public Brush KindBadgeBrush { get; init; }        // 类型徽章底色（半透明）
    public string Name { get; init; } = "";
    public string StatusText { get; init; } = "";     // "已连接" / "未连接"
    public Brush StatusBrush { get; init; }
    /// <summary>IP · 链路速率</summary>
    public string IpText { get; init; } = "";
    public string MacText { get; init; } = "";        // "MAC AA-BB-CC-DD-EE-FF"
}

/// <summary>系统信息仪表盘：仅在页面可见时轮询（1s 快速项；WMI/进程统计走后台线程）。
/// 每次采样完成触发 Ticked，页面据此重绘趋势图。</summary>
public class SystemViewModel : ObservableObject
{
    /// <summary>每秒采样完成（UI 线程触发）</summary>
    public event Action Ticked;

    private const int MaxSamples = 300;   // 存满 5 分钟，展示窗口可切换 1/5 分钟

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tick;

    // ---- 图表展示窗口（秒）：60 / 300 ----
    private int _chartWindowSec = 60;
    public int ChartWindowSec
    {
        get => _chartWindowSec;
        private set
        {
            if (SetProperty(ref _chartWindowSec, value))
            {
                OnPropertyChanged(nameof(ChartSpanText));
                OnPropertyChanged(nameof(ChartWindowText));
            }
        }
    }
    public string ChartSpanText => ChartWindowSec == 60 ? "最近 1 分钟" : "最近 5 分钟";
    public string ChartWindowText => ChartWindowSec == 60 ? "窗口 1 分钟" : "窗口 5 分钟";
    public void ToggleChartWindow() => ChartWindowSec = ChartWindowSec == 60 ? 300 : 60;

    private NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _cpuBase;
    private long _netBaseDown, _netBaseUp, _netBaseMs;

    private readonly Queue<double> _cpuHist = new();
    private readonly Queue<double> _cpuTempHist = new();   // CPU 温度曲线（5s 一点）
    private readonly Queue<double> _memHist = new();
    private readonly Queue<double> _gpuHist = new();
    private readonly Queue<double> _netDownHist = new();
    private readonly Queue<double> _netUpHist = new();

    // ---- 温度色阶（正常低调 / 温暖琥珀 / 过热红），冻结后可跨线程安全使用 ----
    private static readonly Brush TempOkBrush = Freeze("#FF9AA6B8");
    private static readonly Brush TempWarmBrush = Freeze("#FFEFAA17");
    private static readonly Brush TempHotBrush = Freeze("#FFE8463A");
    private static Brush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }
    private static Brush TempBrush(double? t) => t switch
    {
        >= 80 => TempHotBrush,
        >= 60 => TempWarmBrush,
        _ => TempOkBrush
    };

    // ---- 网卡类型/状态色（有线琥珀 · 无线绿 · 已连接绿 · 未连接灰），冻结后跨线程安全 ----
    private static readonly Brush WiredBrush = Freeze("#FFD9A441");
    private static readonly Brush WirelessBrush = Freeze("#FF6CCB5F");
    private static readonly Brush WiredBadgeBrush = Freeze("#33D9A441");
    private static readonly Brush WirelessBadgeBrush = Freeze("#336CCB5F");
    private static readonly Brush NicUpBrush = Freeze("#FF6CCB5F");
    private static readonly Brush NicDownBrush = Freeze("#FF5F6570");

    // ---- 静态信息（构造时采集一次） ----
    public string CpuName { get; }
    public string CpuFooter { get; }
    public string OsText { get; }
    public bool GpuAvailable { get; }
    public string GpuNamesText { get; }
    public string GpuHintText { get; }
    public List<DiskCellVm> Disks { get; private set; } = new();

    private string _memSpecText = "";
    public string MemSpecText { get => _memSpecText; private set => SetProperty(ref _memSpecText, value); }

    private string _boardBiosText = "";
    public string BoardBiosText { get => _boardBiosText; private set => SetProperty(ref _boardBiosText, value); }

    private string _osInstallText = "";
    public string OsInstallText { get => _osInstallText; private set => SetProperty(ref _osInstallText, value); }

    private string _nicText = "";
    public string NicText { get => _nicText; private set => SetProperty(ref _nicText, value); }

    // ---- 显示器（型号/尺寸/接口/当前分辨率） ----
    private List<MonitorVm> _monitors = new();
    public List<MonitorVm> Monitors { get => _monitors; private set => SetProperty(ref _monitors, value); }

    private string _monitorFootText = "";
    public string MonitorFootText { get => _monitorFootText; private set => SetProperty(ref _monitorFootText, value); }

    // ---- 网络设备（有线/无线网卡清单） ----
    private List<NicVm> _nics = new();
    public List<NicVm> Nics { get => _nics; private set => SetProperty(ref _nics, value); }

    // ---- 显卡驱动版本 ----
    private string _gpuDriverText = "";
    public string GpuDriverText { get => _gpuDriverText; private set => SetProperty(ref _gpuDriverText, value); }

    private string _monitorSig = "";   // 显示器内容签名（变化才重建集合，避免闪烁）
    private string _nicSig = "";

    private double? _lastCpuTemp, _lastGpuTemp, _lastDiskMaxTemp;   // 色阶与报告用
    private Dictionary<string, string> _diskHealth = new();
    private DateTime? _osInstallDate;

    // ---- CPU ----
    private double _cpuUsage;
    public double CpuUsage { get => _cpuUsage; private set => SetProperty(ref _cpuUsage, value); }

    private string _cpuUsageText = "--%";
    public string CpuUsageText { get => _cpuUsageText; private set => SetProperty(ref _cpuUsageText, value); }

    private string _cpuTempText = "温度 —";
    public string CpuTempText { get => _cpuTempText; private set => SetProperty(ref _cpuTempText, value); }

    private Brush _cpuTempBrush = TempOkBrush;
    public Brush CpuTempBrush { get => _cpuTempBrush; private set => SetProperty(ref _cpuTempBrush, value); }

    private string _cpuLiveText = "";   // 实时频率 · 功耗
    public string CpuLiveText { get => _cpuLiveText; private set => SetProperty(ref _cpuLiveText, value); }

    private double[] _perCore = Array.Empty<double>();   // 每逻辑核占用%（迷你条用）
    public double[] PerCoreUsage { get => _perCore; private set => SetProperty(ref _perCore, value); }

    // ---- 内存 ----
    private double _memPct;
    public double MemPct { get => _memPct; private set => SetProperty(ref _memPct, value); }

    private string _memPctText = "--%";
    public string MemPctText { get => _memPctText; private set => SetProperty(ref _memPctText, value); }

    private string _memUsedText = "— / — GB";
    public string MemUsedText { get => _memUsedText; private set => SetProperty(ref _memUsedText, value); }

    private string _memCommitText = "已提交 — / — GB";
    public string MemCommitText { get => _memCommitText; private set => SetProperty(ref _memCommitText, value); }

    // ---- GPU ----
    private double _gpuUsage;
    public double GpuUsage { get => _gpuUsage; private set => SetProperty(ref _gpuUsage, value); }

    private string _gpuUsageText = "—";
    public string GpuUsageText { get => _gpuUsageText; private set => SetProperty(ref _gpuUsageText, value); }

    private string _gpuTempText = "温度 —";
    public string GpuTempText { get => _gpuTempText; private set => SetProperty(ref _gpuTempText, value); }

    private Brush _gpuTempBrush = TempOkBrush;
    public Brush GpuTempBrush { get => _gpuTempBrush; private set => SetProperty(ref _gpuTempBrush, value); }

    private string _gpuMemText = "显存 — / — GB";
    public string GpuMemText { get => _gpuMemText; private set => SetProperty(ref _gpuMemText, value); }

    // ---- 网络 ----
    private string _netDownText = "0 KB/s";
    public string NetDownText { get => _netDownText; private set => SetProperty(ref _netDownText, value); }

    private string _netUpText = "0 KB/s";
    public string NetUpText { get => _netUpText; private set => SetProperty(ref _netUpText, value); }

    private string _netTotalText = "开机累计 ↓ — ↑ —";
    public string NetTotalText { get => _netTotalText; private set => SetProperty(ref _netTotalText, value); }

    // ---- 系统 ----
    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }

    private string _sysStatsText = "";
    public string SysStatsText { get => _sysStatsText; private set => SetProperty(ref _sysStatsText, value); }

    private string _diskTempsText = "";
    public string DiskTempsText { get => _diskTempsText; private set => SetProperty(ref _diskTempsText, value); }

    private Brush _diskTempBrush = TempOkBrush;
    public Brush DiskTempBrush { get => _diskTempBrush; private set => SetProperty(ref _diskTempBrush, value); }

    public SystemViewModel()
    {
        CpuName = SystemInfoService.GetCpuName();
        var ghz = SystemInfoService.GetCpuGHz();
        var top = CpuTopology.HasECore
            ? $"{CpuTopology.PCoreCount} P核 + {CpuTopology.ECoreCount} E核 · {CpuTopology.LogicalCount} 线程"
            : $"{CpuTopology.LogicalCount} 线程 · {CpuTopology.PCoreCount} 核心";
        CpuFooter = (ghz > 0 ? $"基础频率 {ghz:0.00} GHz · " : "") + top;
        OsText = SystemInfoService.GetOsDisplay();

        GpuAvailable = GpuService.Available;
        var gpus = GpuAvailable ? GpuService.Names : SystemInfoService.GetGpuNames();
        GpuNamesText = gpus.Count > 0 ? string.Join(" · ", gpus) : "未检测到独立显卡";
        GpuHintText = GpuAvailable
            ? "GPU 占用率 · 最近 60 秒"
            : "实时占用与温度需 NVIDIA 显卡（NVML）支持";

        _timer.Tick += (s, e) => Tick();

        // 一次性静态信息走后台（WMI 冷启动可达数百 ms，不阻塞 UI 线程）
        _ = LoadStaticInfoAsync();
    }

    /// <summary>内存规格 / 主板 BIOS / 磁盘健康 / 装机日期 / 显卡驱动 / 显示器：后台采集一次</summary>
    private async Task LoadStaticInfoAsync()
    {
        var memSpec = await Task.Run(SystemInfoService.GetMemorySpec);
        if (!string.IsNullOrEmpty(memSpec)) MemSpecText = memSpec;

        var board = await Task.Run(SystemInfoService.GetBoardBios);
        if (!string.IsNullOrEmpty(board)) BoardBiosText = board;

        _diskHealth = await Task.Run(SystemInfoService.GetDiskHealth);

        _osInstallDate = await Task.Run(SystemInfoService.GetOsInstallDate);
        if (_osInstallDate is { } d)
            OsInstallText = $"装机 {d:yyyy-MM-dd} · 主机 {Environment.MachineName}";

        // 显卡驱动版本：多卡以 " · " 连接，如 "561.09 (2025-07) · 31.0.101.5333 (2024-12)"
        var drivers = await Task.Run(SystemInfoService.GetGpuDrivers);
        var dParts = drivers
            .Select(g => string.IsNullOrEmpty(g.Date) ? g.Version : $"{g.Version} ({g.Date})")
            .Where(x => x.Length > 0).ToList();
        if (dParts.Count > 0) GpuDriverText = $"驱动 {string.Join(" · ", dParts)}";

        await QueryMonitorsCoreAsync();
    }

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _cpuBase = NativeMethods.QueryProcessorTimes();
        (_netBaseDown, _netBaseUp, _netBaseMs) = ReadNetTotal();
        _tick = 0;
        _timer.Start();
        Tick(); // 立即出首个读数
    }

    public void Stop() => _timer.Stop();

    // ---- 历史快照（供页面绘图） ----
    public double[] CpuHistory() => _cpuHist.ToArray();
    public double[] MemHistory() => _memHist.ToArray();
    public double[] GpuHistory() => _gpuHist.ToArray();
    public double[] NetDownHistory() => _netDownHist.ToArray();
    public double[] NetUpHistory() => _netUpHist.ToArray();

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > MaxSamples) q.Dequeue();
    }

    private void Tick()
    {
        _tick++;
        SampleCpu();
        SampleMemory();
        SampleNetwork();
        SampleGpu();

        if (_tick == 1 || _tick % 5 == 0) QueryCpuMetricsAsync();    // 5s：温度/频率/功耗
        if (_tick == 1 || _tick % 10 == 0) RefreshUptimeAndDisks();  // 10s（同步，DriveInfo 很快）
        if (_tick == 1 || _tick % 10 == 0) QueryProcStatsAsync();    // 10s（后台）
        if (_tick == 1 || _tick % 10 == 0) QueryNicAsync();          // 10s（后台，含网卡清单）
        if (_tick == 1 || _tick % 30 == 0) QueryDiskTempsAsync();    // 30s（后台）
        if (_tick % 30 == 0) QueryMonitorsAsync();                   // 30s（后台，分辨率/接口变化跟随）

        Ticked?.Invoke();
    }

    // ---- CPU：Δ(核+用户-空闲) / Δ(核+用户)，同时保留每核占用 ----
    private void SampleCpu()
    {
        var cur = NativeMethods.QueryProcessorTimes();
        if (cur == null || _cpuBase == null || cur.Length != _cpuBase.Length) return;

        double sumBusy = 0, sumTotal = 0;
        var perCore = new double[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            double dk = cur[i].KernelTime - _cpuBase[i].KernelTime;
            double du = cur[i].UserTime - _cpuBase[i].UserTime;
            double di = cur[i].IdleTime - _cpuBase[i].IdleTime;
            double busy = Math.Max(0, dk - di + du), total = dk + du;
            sumBusy += busy;
            sumTotal += total;
            if (total > 0) perCore[i] = Math.Clamp(busy / total * 100, 0, 100);
        }
        _cpuBase = cur;

        if (sumTotal > 0)
        {
            CpuUsage = Math.Clamp(sumBusy / sumTotal * 100, 0, 100);
            CpuUsageText = $"{CpuUsage:0}%";
            Push(_cpuHist, CpuUsage);
        }
        PerCoreUsage = perCore;
    }

    private void SampleMemory()
    {
        var m = NativeMethods.GetMemoryStatus();
        MemPct = m.MemoryLoad;
        MemPctText = $"{m.MemoryLoad}%";
        MemUsedText = $"{(m.TotalPhys - m.AvailPhys) / 1073741824.0:0.0} / {m.TotalPhys / 1073741824.0:0.0} GB";
        MemCommitText = $"已提交 {(m.TotalPageFile - m.AvailPageFile) / 1073741824.0:0.0} / {m.TotalPageFile / 1073741824.0:0.0} GB";
        Push(_memHist, m.MemoryLoad);
    }

    // ---- 网络：接口累计字节数差值 = 速率 ----
    private void SampleNetwork()
    {
        var (down, up, ms) = ReadNetTotal();
        double sec = (ms - _netBaseMs) / 1000.0;
        if (sec > 0.2)
        {
            var dSpeed = Math.Max(0, (down - _netBaseDown) / sec);
            var uSpeed = Math.Max(0, (up - _netBaseUp) / sec);
            NetDownText = FmtSpeed(dSpeed);
            NetUpText = FmtSpeed(uSpeed);
            NetTotalText = $"开机累计 ↓ {FmtBytes(down)} ↑ {FmtBytes(up)}";
            Push(_netDownHist, dSpeed);
            Push(_netUpHist, uSpeed);
            _netBaseDown = down; _netBaseUp = up; _netBaseMs = ms;
        }
    }

    // ---- GPU（NVML，进程内调用） ----
    private void SampleGpu()
    {
        if (!GpuAvailable) return;
        var s = GpuService.Query(0);
        if (s == null) return;
        GpuUsage = s.UsagePct;
        GpuUsageText = $"{s.UsagePct}%";
        _lastGpuTemp = s.TempC > 0 ? s.TempC : null;
        GpuTempText = s.TempC > 0 ? $"温度 {s.TempC}°C" : "温度 —";
        GpuTempBrush = TempBrush(_lastGpuTemp);
        if (s.TotalGb > 0) GpuMemText = $"显存 {s.UsedGb:0.0} / {s.TotalGb:0.0} GB";
        Push(_gpuHist, s.UsagePct);
    }

    // ---- 后台采样（await 后自动回到 UI 同步上下文，可直接赋值） ----

    private async void QueryCpuMetricsAsync()
    {
        var m = await Task.Run(() => TemperatureService.QueryCpuMetrics());
        _lastCpuTemp = m.TempC;
        CpuTempText = m.TempC == null ? "温度 —"
            : m.Source == "core" ? $"温度 {m.TempC:0}°C"      // CPU 核心 DTS（准确）
            : $"温度 {m.TempC:0}°C（主板）";                    // ACPI 热区回退，注明口径
        CpuTempBrush = TempBrush(m.TempC);

        var live = new List<string>();
        if (m.ClockGHz > 0) live.Add($"{m.ClockGHz:0.00} GHz");
        if (m.PowerW > 0) live.Add($"{m.PowerW:0.#} W");
        if (m.Source == "core" && m.MaxCoreTemp is { } mc && m.TempC is { } pk && mc - pk >= 1)
            live.Add($"最热核 {mc:0}°C");
        CpuLiveText = string.Join(" · ", live);
    }

    private async void QueryProcStatsAsync()
    {
        var text = await Task.Run(() =>
        {
            try
            {
                var ps = Process.GetProcesses();
                long threads = 0, handles = 0;
                foreach (var p in ps)
                {
                    try { threads += p.Threads.Count; handles += p.HandleCount; } catch { /* 受保护进程跳过 */ }
                }
                return $"进程 {ps.Length:N0} · 线程 {threads:N0} · 句柄 {handles:N0}";
            }
            catch { return ""; }
        });
        if (!string.IsNullOrEmpty(text)) SysStatsText = text;
    }

    private async void QueryDiskTempsAsync()
    {
        var temps = await Task.Run(() => TemperatureService.QueryDiskTemps());
        if (temps.Count == 0)
        {
            DiskTempsText = "";
            return;
        }
        var names = TemperatureService.DiskNames;
        double maxTemp = 0;
        var parts = temps
            .OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 99)
            .Select(kv =>
            {
                var nm = names.TryGetValue(kv.Key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : $"磁盘 {kv.Key}";
                if (nm.Length > 24) nm = nm[..24] + "…";
                if (kv.Value > maxTemp) maxTemp = kv.Value;
                // 拼健康标签：名称 45°C · SSD·NVMe · 健康 98%
                var health = _diskHealth.TryGetValue(kv.Key, out var h) ? $" · {h}" : "";
                return $"{nm} {kv.Value}°C{health}";
            });
        DiskTempsText = "物理盘  " + string.Join(" · ", parts);
        _lastDiskMaxTemp = maxTemp;
        DiskTempBrush = TempBrush(maxTemp);
    }

    /// <summary>活动网卡信息：挑流量最大的 Up 接口（以太网/WiFi · 名称 · 链路速率 · IPv4）；
    /// 同时刷新网络设备清单（有线/无线 · IP · MAC · 连接状态）</summary>
    private async void QueryNicAsync()
    {
        var (bestText, nics) = await Task.Run(() =>
        {
            try
            {
                NetworkInformation best = null;
                long bestBytes = -1;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var st = nic.GetIPv4Statistics();
                    var total = st.BytesReceived + st.BytesSent;
                    if (total > bestBytes) { bestBytes = total; best = new NetworkInformation(nic); }
                }
                return (best?.Describe() ?? "", SystemInfoService.GetNetworkAdapters());
            }
            catch { return ("", new List<NicInfo>()); }
        });
        if (!string.IsNullOrEmpty(bestText)) NicText = bestText;

        if (nics.Count > 0)
        {
            var sig = string.Concat(nics.Select(n => $"{n.Name}|{n.Ip}|{n.Up}|{n.Speed}"));
            if (sig != _nicSig)
            {
                _nicSig = sig;
                Nics = nics.Select(ToNicVm).ToList();
            }
        }
    }

    /// <summary>网卡摘要（一次性快照）</summary>
    private sealed class NetworkInformation
    {
        private readonly NetworkInterface _nic;
        public NetworkInformation(NetworkInterface nic) => _nic = nic;

        public string Describe()
        {
            var type = _nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "WiFi",
                NetworkInterfaceType.Ethernet => "以太网",
                _ => ""
            };
            var speed = _nic.Speed switch
            {
                >= 1_000_000_000 => $"{_nic.Speed / 1_000_000_000.0:0.#} Gbps",
                >= 1_000_000 => $"{_nic.Speed / 1_000_000:0} Mbps",
                _ => ""
            };
            string ip = null;
            try
            {
                ip = _nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address.ToString();
            }
            catch { /* 属性不可读时省略 IP */ }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(type)) parts.Add(type);
            if (!string.IsNullOrEmpty(speed)) parts.Add(speed);
            if (!string.IsNullOrEmpty(ip)) parts.Add(ip);
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }
    }

    /// <summary>显示器清单：型号/尺寸/接口/当前分辨率（后台线程采集，内容变化才重建）</summary>
    private async void QueryMonitorsAsync() => await QueryMonitorsCoreAsync();

    private async Task QueryMonitorsCoreAsync()
    {
        var list = await Task.Run(SystemInfoService.GetMonitors);
        if (list.Count == 0) return;

        var sig = string.Concat(list.Select(m => $"{m.Name}|{m.Width}x{m.Height}|{m.Hz}|{m.ScalePct:0}|{m.IsPrimary}|{m.Connection}|{m.Inches:0.#}"));
        if (sig == _monitorSig) return;
        _monitorSig = sig;

        Monitors = list.Select(ToMonitorVm).ToList();
        var primary = list.FirstOrDefault(m => m.IsPrimary) ?? list[0];
        MonitorFootText = primary.Width > 0
            ? $"{list.Count} 台显示器 · 主屏 {primary.Width}×{primary.Height}{(primary.Hz > 1 ? $" @ {primary.Hz}Hz" : "")}"
            : $"{list.Count} 台显示器";
    }

    private static MonitorVm ToMonitorVm(MonitorInfo m)
    {
        var tag = new List<string>();
        if (!string.IsNullOrEmpty(m.Connection)) tag.Add(m.Connection);
        if (m.Inches > 0) tag.Add($"{m.Inches:0.#}\"");
        var mode = new List<string>();
        if (m.Width > 0)
            mode.Add($"{m.Width}×{m.Height}{(m.Hz > 1 ? $" @ {m.Hz}Hz" : "")}");
        if (m.ScalePct > 0) mode.Add($"缩放 {m.ScalePct:0}%");
        return new MonitorVm
        {
            Name = string.IsNullOrWhiteSpace(m.Name) ? "通用即插即用显示器" : m.Name,
            IsPrimary = m.IsPrimary,
            TagText = string.Join(" · ", tag),
            ModeText = string.Join(" · ", mode)
        };
    }

    private static NicVm ToNicVm(NicInfo n)
    {
        var ip = new List<string>();
        if (!string.IsNullOrEmpty(n.Ip)) ip.Add(n.Ip);
        if (!string.IsNullOrEmpty(n.Speed)) ip.Add(n.Speed);
        return new NicVm
        {
            KindText = n.Wireless ? "无线" : "有线",
            KindBrush = n.Wireless ? WirelessBrush : WiredBrush,
            KindBadgeBrush = n.Wireless ? WirelessBadgeBrush : WiredBadgeBrush,
            Name = n.Name,
            StatusText = n.Up ? "已连接" : "未连接",
            StatusBrush = n.Up ? NicUpBrush : NicDownBrush,
            IpText = string.Join(" · ", ip),
            MacText = string.IsNullOrEmpty(n.Mac) ? "" : $"MAC {n.Mac}"
        };
    }

    private void RefreshUptimeAndDisks()
    {
        var disks = SystemInfoService.GetDisks();
        if (Disks.Count != disks.Count)
        {
            Disks = disks.Select(d => new DiskCellVm { Letter = d.Letter, Label = d.Label }).ToList();
            OnPropertyChanged(nameof(Disks));
        }
        for (int i = 0; i < disks.Count && i < Disks.Count; i++)
            Disks[i].Update(disks[i].UsedGb, disks[i].TotalGb, disks[i].UsedPct);

        var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText = ts.Days > 0 ? $"开机 {ts.Days} 天 {ts.Hours} 小时"
            : ts.Hours > 0 ? $"开机 {ts.Hours} 小时 {ts.Minutes} 分"
            : $"开机 {ts.Minutes} 分 {ts.Seconds} 秒";
    }

    private static (long down, long up, long ms) ReadNetTotal()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = nic.GetIPv4Statistics();
                down += s.BytesReceived;
                up += s.BytesSent;
            }
        }
        catch { /* 统计不可用时保持 0 */ }
        return (down, up, Environment.TickCount64);
    }

    internal static string FmtSpeed(double bytesPerSec) => bytesPerSec >= 1048576
        ? $"{bytesPerSec / 1048576:0.00} MB/s"
        : bytesPerSec >= 1024 ? $"{bytesPerSec / 1024:0.0} KB/s" : $"{bytesPerSec:0} B/s";

    internal static string FmtBytes(double b) => b >= 1099511627776
        ? $"{b / 1099511627776:0.##} TB"
        : b >= 1073741824 ? $"{b / 1073741824:0.#} GB" : $"{b / 1048576:0.#} MB";

    // ============ 复制系统报告 ============

    /// <summary>汇总当前全部信息为纯文本报告（分享配置 / 求助场景）</summary>
    public string BuildReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ProcOpt 系统报告  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("────────────────────────────");
        sb.AppendLine($"操作系统：{OsText}{(string.IsNullOrEmpty(OsInstallText) ? "" : $" · {OsInstallText}")}");
        sb.AppendLine($"处理器：{CpuName} · {CpuFooter}{(string.IsNullOrEmpty(CpuLiveText) ? "" : $" · 当前 {CpuLiveText}")}");
        if (!string.IsNullOrEmpty(BoardBiosText)) sb.AppendLine($"主板：{BoardBiosText}");
        sb.AppendLine($"内存：{MemUsedText} ({MemPctText}){(string.IsNullOrEmpty(MemSpecText) ? "" : $" · {MemSpecText}")}");
        sb.AppendLine($"显卡：{GpuNamesText}{(GpuAvailable ? $" · {GpuUsageText} · {GpuTempText} · {GpuMemText}" : "")}");
        if (!string.IsNullOrEmpty(GpuDriverText)) sb.AppendLine(GpuDriverText);
        if (Monitors.Count > 0)
        {
            sb.AppendLine("显示器：");
            foreach (var m in Monitors)
            {
                var line = $"  {m.Name}{(m.IsPrimary ? "（主）" : "")}";
                if (m.TagText.Length > 0) line += $" · {m.TagText}";
                if (m.ModeText.Length > 0) line += $" · {m.ModeText}";
                sb.AppendLine(line);
            }
        }
        if (Nics.Count > 0)
        {
            sb.AppendLine("网络设备：");
            foreach (var n in Nics)
                sb.AppendLine($"  [{n.KindText}] {n.Name} · {n.StatusText}{(n.IpText.Length > 0 ? $" · {n.IpText}" : "")}{(n.MacText.Length > 0 ? $" · {n.MacText}" : "")}");
        }
        if (!string.IsNullOrEmpty(DiskTempsText)) sb.AppendLine($"磁盘：{DiskTempsText[4..]}");   // 去掉"物理盘"前缀
        if (Disks.Count > 0)
        {
            sb.AppendLine("分区：");
            foreach (var d in Disks)
                sb.AppendLine($"  {d.Letter} {d.Label} {d.Summary} ({d.UsedPct:0}%)");
        }
        if (!string.IsNullOrEmpty(NicText)) sb.AppendLine($"网络：{NicText} · 当前 ↓{NetDownText} ↑{NetUpText}");
        if (!string.IsNullOrEmpty(SysStatsText)) sb.AppendLine(SysStatsText);
        sb.AppendLine(UptimeText);
        return sb.ToString();
    }

    /// <summary>复制报告到剪贴板，返回是否成功（供按钮反馈）</summary>
    public bool CopyReport()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildReportText());
            return true;
        }
        catch { return false; }   // 剪贴板被其他进程占用时失败
    }
}
