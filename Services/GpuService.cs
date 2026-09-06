using System.Runtime.InteropServices;
using System.Text;

namespace ProcOpt.Services;

/// <summary>一次 GPU 采样结果</summary>
public class GpuSample
{
    public int UsagePct;   // 0-100
    public int TempC;      // ℃，0 = 未知
    public double UsedGb;  // 显存已用
    public double TotalGb; // 显存总量
    public int FanRpm;     // 风扇转速，0 = 未知（部分卡无风扇/不支持）
}

/// <summary>NVIDIA GPU 采样（NVML 随驱动安装于 System32，进程内调用零开销）。
/// 非 NVIDIA 环境加载失败自动置为不可用，调用方需判 Available。</summary>
public static class GpuService
{
    private const int NvmlSuccess = 0;
    private const int NvmlTempGpu = 0;   // NVML_TEMPERATURE_GPU

    private static readonly List<IntPtr> _devices = new();
    private static readonly bool _available;

    public static bool Available => _available;
    public static List<string> Names { get; } = new();
    public static string DriverVersion { get; private set; } = "";   // 如 "566.36"

    static GpuService()
    {
        try
        {
            if (nvmlInit_v2() != NvmlSuccess) return;
            if (nvmlDeviceGetCount_v2(out uint n) != NvmlSuccess) return;
            for (uint i = 0; i < n && i < 4; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var h) != NvmlSuccess) continue;
                var sb = new StringBuilder(160);
                if (nvmlDeviceGetName(h, sb, 160) == NvmlSuccess && sb.Length > 0)
                    Names.Add(sb.ToString().Trim());
                _devices.Add(h);
            }
            var drv = new StringBuilder(80);
            if (nvmlSystemGetDriverVersion(drv, 80) == NvmlSuccess && drv.Length > 0)
                DriverVersion = drv.ToString().Trim();
            _available = _devices.Count > 0;
        }
        catch { /* nvml.dll 缺失（非 N 卡 / 无驱动）→ 保持不可用 */ }
    }

    /// <summary>采样第 index 块 GPU。失败返回 null。</summary>
    public static GpuSample Query(int index = 0)
    {
        if (!_available || index < 0 || index >= _devices.Count) return null;
        var s = new GpuSample();
        if (nvmlDeviceGetUtilizationRates(_devices[index], out var util) == NvmlSuccess)
            s.UsagePct = (int)Math.Min(100, util.Gpu);
        if (nvmlDeviceGetTemperature(_devices[index], NvmlTempGpu, out uint t) == NvmlSuccess && t > 0 && t < 120)
            s.TempC = (int)t;
        if (nvmlDeviceGetMemoryInfo(_devices[index], out var mem) == NvmlSuccess && mem.Total > 0)
        {
            s.TotalGb = mem.Total / 1073741824.0;
            s.UsedGb = mem.Used / 1073741824.0;
        }
        if (nvmlDeviceGetFanSpeed(_devices[index], out uint fan) == NvmlSuccess && fan > 0 && fan < 20000)
            s.FanRpm = (int)fan;
        return s;
    }

    // ---- NVML 结构与入口 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
        public uint Encoder;
        public uint Decoder;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Used;
        public ulong Free;
    }

    [DllImport("nvml.dll")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr handle);

    [DllImport("nvml.dll", CharSet = CharSet.Ansi)]
    private static extern int nvmlDeviceGetName(IntPtr handle, StringBuilder name, uint length);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr handle, out NvmlUtilization utilization);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetTemperature(IntPtr handle, int sensorType, out uint temp);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr handle, out NvmlMemory memory);

    [DllImport("nvml.dll", CharSet = CharSet.Ansi)]
    private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetFanSpeed(IntPtr handle, out uint speed);
}
