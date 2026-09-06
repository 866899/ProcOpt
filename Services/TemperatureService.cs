using System.Diagnostics;
using System.IO;
using System.Management;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace ProcOpt.Services;

/// <summary>单个核心的实时读数（详情弹窗用）</summary>
/// <param name="Name">如 "Core #3"</param>
public sealed record CoreReading(string Name, double? TempC, double ClockGHz);

/// <summary>CPU 实时指标（一次 LHM Update 同时取，避免重复轮询）</summary>
/// <param name="TempC">封装温度（Intel=Package / AMD=Tctl/Tdie）</param>
/// <param name="Source">"core"=核心 DTS / "acpi"=主板热区回退</param>
/// <param name="MaxCoreTemp">最热核心温度</param>
/// <param name="ClockGHz">当前最高核心频率</param>
/// <param name="PowerW">封装功耗</param>
/// <param name="Cores">每核温度/频率明细（无驱动时为空）</param>
public sealed record CpuMetrics(double? TempC, string Source, double? MaxCoreTemp, double ClockGHz, double PowerW,
    IReadOnlyList<CoreReading> Cores);

/// <summary>温度采集。CPU 温度优先走 LibreHardwareMonitor（PawnIO 内核驱动读 CPU 核心
/// 数字温度传感器 DTS，与 AIDA64/HWiNFO 同源，精度 ±1°C）。PawnIO 缺失时用内嵌的官方
/// 安装器静默安装（本程序已提权，无额外 UAC）；安装或驱动加载失败自动回退 ACPI 热区
/// （主板传感器，精度差）。硬盘温度用存储可靠性计数器。</summary>
public static class TemperatureService
{
    private static Computer _lhm;
    private static readonly List<IHardware> _cpuHws = new();
    private static bool _lhmTried;      // 只尝试初始化一次
    private static bool _cpuSupported = true;   // ACPI 回退开关
    private static Dictionary<string, string> _diskNames;

    /// <summary>CPU 实时指标：温度 + 频率 + 功耗 + 每核明细。</summary>
    public static CpuMetrics QueryCpuMetrics()
    {
        EnsureLhm();
        if (_lhm != null && _cpuHws.Count > 0)
        {
            double package = double.MinValue, coreMax = double.MinValue, clock = 0, power = 0;
            // 每核聚合：Core #N → (温度, 频率)，LHM 传感器按线程粒度返回，同核取最大
            var perCore = new SortedDictionary<int, (double? t, double c)>(Comparer<int>.Create((a, b) => a.CompareTo(b)));
            foreach (var hw in _cpuHws)
            {
                hw.Update();
                foreach (var s in hw.Sensors)
                {
                    if (s.Value is not > 0) continue;
                    double v = s.Value.Value;
                    var n = s.Name;
                    switch (s.SensorType)
                    {
                        case SensorType.Temperature:
                            // Intel: "CPU Package" / AMD: "Tctl"/"Tdie" 为封装温度，最接近任务管理器口径
                            if (n.Contains("Package") || n.Contains("Tctl") || n.Contains("Tdie"))
                            {
                                if (v > package) package = v;
                            }
                            else if (n.StartsWith("CPU Core #"))
                            {
                                if (v > coreMax) coreMax = v;
                                if (TryCoreIndex(n, out var ci))
                                {
                                    var prev = perCore.GetValueOrDefault(ci);
                                    perCore[ci] = (Math.Max(prev.t ?? 0, v), prev.c);
                                }
                            }
                            break;
                        case SensorType.Clock:
                            if (n.StartsWith("CPU Core #") && v > clock) clock = v;   // 各核当前最高频
                            if (TryCoreIndex(n, out var cc))
                            {
                                var prev = perCore.GetValueOrDefault(cc);
                                perCore[cc] = (prev.t, Math.Max(prev.c, v));
                            }
                            break;
                        case SensorType.Power:
                            if (n.Contains("Package") && v > power) power = v; // 封装功耗
                            break;
                    }
                }
            }
            if (package > double.MinValue || coreMax > double.MinValue)
            {
                var cores = perCore
                    .Select(kv => new CoreReading($"核心 {kv.Key + 1}", kv.Value.t, kv.Value.c > 0 ? kv.Value.c / 1000.0 : 0))
                    .ToList();
                return new CpuMetrics(
                    package > double.MinValue ? package : null, "core",
                    coreMax > double.MinValue ? coreMax : null,
                    clock > 0 ? clock / 1000.0 : 0, power, cores);
            }
        }

        var acpi = QueryCpuTempAcpi();
        return new CpuMetrics(acpi, "acpi", null, 0, 0, Array.Empty<CoreReading>());
    }

    /// <summary>"CPU Core #3 [any]" → 3；线程级 "CPU Core #3 Thread #1" 归并到 3</summary>
    private static bool TryCoreIndex(string sensorName, out int index)
    {
        index = 0;
        var m = System.Text.RegularExpressions.Regex.Match(sensorName, @"Core #(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out index);
    }

    /// <summary>懒初始化 LHM（首次调用在后台线程，Open 含驱动加载，几十毫秒）。
    /// 失败原因写入 %AppData%\ProcOpt\lhm_diag.txt 便于诊断。</summary>
    private static void EnsureLhm()
    {
        if (_lhmTried) return;
        lock (_cpuHws)
        {
            if (_lhmTried) return;
            _lhmTried = true;
            try
            {
                var lhmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Diag($"LHM init start. ProcOpt Location=[{lhmLoc}] BaseDir=[{AppContext.BaseDirectory}]");
                EnsurePawnIo();   // 驱动缺失则静默安装，LHM 才能读到核心温度
                _lhm = new Computer { IsCpuEnabled = true };
                _lhm.Open();
                foreach (var hw in _lhm.Hardware)
                    if (hw.HardwareType == HardwareType.Cpu)
                        _cpuHws.Add(hw);
                Diag($"LHM opened. CPU hardware count={_cpuHws.Count}, " +
                     $"sensors=[{string.Join(",", _cpuHws.SelectMany(h => h.Sensors).Select(s => $"{s.Name}:{s.Value}").Take(30))}]");
            }
            catch (Exception ex)
            {
                _lhm = null;
                Diag($"LHM init FAILED: {ex}");
            }
        }
    }

    /// <summary>PawnIO 是否已安装（与 LHM 内部判定一致：读卸载表 DisplayVersion）。</summary>
    private static bool IsPawnIoInstalled()
    {
        try
        {
            const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            if (!string.IsNullOrEmpty(k?.GetValue("DisplayVersion") as string)) return true;
            using var k64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(subKey);
            return !string.IsNullOrEmpty(k64?.GetValue("DisplayVersion") as string);
        }
        catch { return false; }
    }

    /// <summary>PawnIO 缺失时释放内嵌的官方安装器并静默安装（-install -silent）。
    /// 本程序以管理员运行，子进程继承权限，不会弹 UAC。失败只记日志，走 ACPI 回退。</summary>
    private static void EnsurePawnIo()
    {
        if (IsPawnIoInstalled()) { Diag("PawnIO already installed."); return; }
        try
        {
            var setupPath = Path.Combine(Path.GetTempPath(), "ProcOpt_PawnIO_setup.exe");
            using (var rs = typeof(TemperatureService).Assembly
                       .GetManifestResourceStream("ProcOpt.Resources.PawnIO_setup.exe"))
            {
                if (rs == null) { Diag("PawnIO setup resource MISSING."); return; }
                using var fs = File.Create(setupPath);
                rs.CopyTo(fs);
            }
            Diag($"PawnIO missing. Extracted setup [{setupPath}], installing silently...");
            using var p = Process.Start(new ProcessStartInfo(setupPath, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) { Diag("PawnIO setup failed to start."); return; }
            if (!p.WaitForExit(60_000)) { Diag("PawnIO setup TIMEOUT (60s)."); return; }
            Diag($"PawnIO setup exit={(p.ExitCode)} nowInstalled={IsPawnIoInstalled()}");
        }
        catch (Exception ex)
        {
            Diag($"PawnIO install FAILED: {ex.Message}");
        }
    }

    /// <summary>追加诊断日志到 %AppData%\ProcOpt\lhm_diag.txt（保留最近 50 条）</summary>
    private static void Diag(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcOpt", "lhm_diag.txt");
            var lines = new List<string> { $"[{DateTime.Now:HH:mm:ss.fff}] {msg}" };
            if (File.Exists(path))
                lines.AddRange(File.ReadAllLines(path).Take(49));
            File.WriteAllLines(path, lines);
        }
        catch { /* 诊断失败不影响主流程 */ }
    }

    /// <summary>ACPI 热区温度（各热区最高值）。主板未暴露热区时返回 null（此后不再重试）。</summary>
    private static double? QueryCpuTempAcpi()
    {
        if (!_cpuSupported) return null;
        try
        {
            double best = double.MinValue;
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject o in searcher.Get())
            {
                if (o["CurrentTemperature"] == null) continue;
                var c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15; // 原始值 = 开尔文×10
                if (c > 0 && c < 120 && c > best) best = c;
            }
            return best > double.MinValue ? best : null;
        }
        catch
        {
            _cpuSupported = false;  // 该主板不支持 ACPI 热区，避免反复查询
            return null;
        }
    }

    /// <summary>各物理磁盘温度（PhysicalDisk DeviceId → ℃）。NVMe/SATA 由驱动上报，部分硬盘不支持。</summary>
    public static Dictionary<string, int> QueryDiskTemps()
    {
        var result = new Dictionary<string, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject o in searcher.Get())
            {
                var dev = o["DeviceId"]?.ToString();
                if (dev == null || o["Temperature"] == null) continue;
                var t = Convert.ToInt32(o["Temperature"]);
                if (t > 0 && t < 100 && !result.ContainsKey(dev)) result[dev] = t;
            }
        }
        catch { /* 存储提供程序不可用时留空 */ }
        return result;
    }

    /// <summary>物理盘 DeviceId → 友好名称（进程内缓存一次）</summary>
    public static Dictionary<string, string> DiskNames
    {
        get
        {
            if (_diskNames != null) return _diskNames;
            var names = new Dictionary<string, string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, FriendlyName FROM MSFT_PhysicalDisk");
                foreach (ManagementObject o in searcher.Get())
                {
                    var dev = o["DeviceId"]?.ToString();
                    var name = o["FriendlyName"]?.ToString();
                    if (dev != null && !string.IsNullOrWhiteSpace(name)) names[dev] = name;
                }
            }
            catch { }
            _diskNames = names;
            return names;
        }
    }
}
