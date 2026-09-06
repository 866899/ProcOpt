using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ProcOpt.Services;

/// <summary>磁盘分区信息快照</summary>
public class DiskInfo
{
    public string Letter { get; set; }        // "C:"
    public string Label { get; set; }         // 卷标
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double UsedPct { get; set; }
}

/// <summary>显示器信息（型号来自 EDID，分辨率来自当前显示模式）</summary>
public class MonitorInfo
{
    public string Name { get; set; } = "";       // 型号（EDID 用户友好名）
    public string Connection { get; set; } = ""; // HDMI / DisplayPort / VGA / DVI / 内屏
    public double Inches { get; set; }           // 对角线英寸（0=未知）
    public int Width { get; set; }               // 当前物理分辨率
    public int Height { get; set; }
    public int Hz { get; set; }                  // 当前刷新率
    public double ScalePct { get; set; }         // 缩放百分比（0=未知）
    public bool IsPrimary { get; set; }
}

/// <summary>网卡信息（仅物理有线/无线网卡）</summary>
public class NicInfo
{
    public bool Wireless { get; set; }
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";         // IPv4 优先，无则全局 IPv6
    public string Mac { get; set; } = "";        // "AA-BB-CC-DD-EE-FF"
    public string Speed { get; set; } = "";      // 链路速率（仅已连接）
    public bool Up { get; set; }
}

/// <summary>显卡驱动条目</summary>
public class GpuDriverInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";    // 美化后版本号
    public string Date { get; set; } = "";       // "yyyy-MM"
}

/// <summary>系统静态信息采集（CPU 型号/频率、显卡、OS 版本、磁盘），全部走注册表与 DriveInfo，无 WMI 开销</summary>
public static class SystemInfoService
{
    private const string CpuKey = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static string GetCpuName()
    {
        var name = Registry.GetValue(CpuKey, "ProcessorNameString", "") as string;
        return string.IsNullOrWhiteSpace(name) ? "未知处理器" : name.Trim();
    }

    /// <summary>基础频率（GHz），如 2.90</summary>
    public static double GetCpuGHz()
    {
        if (Registry.GetValue(CpuKey, "~MHz", null) is int mhz && mhz > 0)
            return mhz / 1000.0;
        return 0;
    }

    /// <summary>枚举显示适配器名称（可能有多个，含核显）</summary>
    public static List<string> GetGpuNames()
    {
        var result = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return result;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(sub, @"^\d{4}$")) continue;
                var desc = cls.OpenSubKey(sub)?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                desc = desc.Trim();
                if (!result.Contains(desc))
                    result.Add(desc);
            }
        }
        catch { /* 注册表不可读时留空 */ }
        return result;
    }

    /// <summary>OS 显示串，如 "Windows 11 专业版 · 24H2 · 26100.8875"</summary>
    public static string GetOsDisplay()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k == null) return Environment.OSVersion.VersionString;

            var product = k.GetValue("ProductName") as string ?? "Windows";
            var build = k.GetValue("CurrentBuildNumber") as string ?? "";
            var disp = k.GetValue("DisplayVersion") as string;
            if (int.TryParse(build, out var b) && b >= 22000 && product.StartsWith("Windows 10"))
                product = product.Replace("Windows 10", "Windows 11");

            var ubr = k.GetValue("UBR") is int u ? $".{u}" : "";
            var parts = new List<string> { product };
            if (!string.IsNullOrEmpty(disp)) parts.Add(disp);
            if (!string.IsNullOrEmpty(build)) parts.Add($"编译 {build}{ubr}");
            return string.Join(" · ", parts);
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    /// <summary>固定磁盘分区用量</summary>
    public static List<DiskInfo> GetDisks()
    {
        var result = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                double total = d.TotalSize / 1073741824.0;
                double free = d.TotalFreeSpace / 1073741824.0;
                result.Add(new DiskInfo
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel,
                    TotalGb = total,
                    UsedGb = total - free,
                    UsedPct = total > 0 ? (total - free) / total * 100 : 0
                });
            }
            catch { /* 盘不可读则跳过 */ }
        }
        return result;
    }

    // ============ 以下为一次性静态信息（WMI，构造后台采集，进程内缓存） ============

    /// <summary>内存规格，如 "2 × 16GB DDR4-3200 · 插槽 2/4 · Samsung"</summary>
    public static string GetMemorySpec()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Capacity, Speed, SMBIOSMemoryType, Manufacturer FROM Win32_PhysicalMemory");
            ulong total = 0; int count = 0; uint speed = 0; uint type = 0; string maker = "";
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                count++;
                total += (ulong)o["Capacity"];
                speed = Math.Max(speed, Convert.ToUInt32(o["Speed"]));
                type = Math.Max(type, Convert.ToUInt32(o["SMBIOSMemoryType"]));
                if (string.IsNullOrEmpty(maker)) maker = o["Manufacturer"]?.ToString()?.Trim() ?? "";
            }
            if (count == 0) return "";

            using var arr = new System.Management.ManagementObjectSearcher(
                "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            int slots = 0;
            foreach (System.Management.ManagementObject o in arr.Get())
                slots = Math.Max(slots, Convert.ToInt32(o["MemoryDevices"]));

            var ddr = type switch
            {
                20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4",
                34 => "DDR5", 35 => "LPDDR5", 30 => "LPDDR4", _ => ""
            };
            var sizePer = total / (ulong)count / 1073741824.0;
            var parts = new List<string> { $"{count} × {sizePer:0.#}GB" };
            if (!string.IsNullOrEmpty(ddr)) parts.Add($"{ddr}-{speed}");
            if (slots > 0) parts.Add($"插槽 {count}/{slots}");
            if (!string.IsNullOrEmpty(maker) && maker != "Unknown") parts.Add(maker);
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    /// <summary>主板 + BIOS，如 "ASUS PRIME H510M-K · BIOS 1801 (2024-05)"</summary>
    public static string GetBoardBios()
    {
        try
        {
            string board = "", bios = "", date = "";
            using (var s = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    var m = o["Manufacturer"]?.ToString()?.Trim();
                    var p = o["Product"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(m) && m != "To Be Filled By O.E.M.")
                        board = string.Join(" ", new[] { m, p }.Where(x => !string.IsNullOrEmpty(x)));
                    break;
                }
            using (var s = new System.Management.ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    bios = o["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    var rd = o["ReleaseDate"]?.ToString();   // DMTF: 20240501000000.000000+480
                    if (rd != null && rd.Length >= 8 && DateTime.TryParseExact(rd[..8], "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.None, out var d))
                        date = d.ToString("yyyy-MM");
                    break;
                }
            var result = board;
            if (!string.IsNullOrEmpty(bios)) result += $" · BIOS {bios}{(string.IsNullOrEmpty(date) ? "" : $" ({date})")}";
            return result;
        }
        catch { return ""; }
    }

    /// <summary>系统安装日期（注册表 InstallDate，Unix 秒）</summary>
    public static DateTime? GetOsInstallDate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallDate") is int sec)
                return DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
        }
        catch { }
        return null;
    }

    /// <summary>当前电源方案名称，如 "电源计划：平衡"（注册表 GUID 一次性读取）</summary>
    public static string GetPowerScheme()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            var guid = (k?.GetValue("ActivePowerScheme") as string)?.Trim('{', '}');
            if (string.IsNullOrEmpty(guid)) return "";
            var name = guid.ToLowerInvariant() switch
            {
                "381b4222-f694-41f0-9685-ff5bb260df2e" => "平衡",
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "高性能",
                "a1841308-3541-4fab-bc81-f71556f20b4a" => "节能",
                "e9a42b02-d5df-448d-aa00-03f14749eb61" => "卓越性能",
                _ => null
            };
            return name == null ? "" : $"电源计划：{name}";
        }
        catch { return ""; }
    }

    /// <summary>安全/虚拟化状态："Secure Boot 开 · HVCI 关"</summary>
    public static string GetSecurityStatus()
    {
        var parts = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            parts.Add(k?.GetValue("UEFISecureBootEnabled") is int sb && sb == 1 ? "Secure Boot 开" : "Secure Boot 关");
        }
        catch { /* 旧 BIOS 无该键 */ }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning, VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                // SecurityServicesRunning 含 2 = HVCI 运行中；VBS Status 2 = 运行
                var svc = o["SecurityServicesRunning"] as ushort[];
                bool hvci = svc != null && svc.Contains((ushort)2);
                bool vbs = o["VirtualizationBasedSecurityStatus"] is ushort v && v == 2;
                if (hvci) parts.Add("HVCI 开");
                else if (vbs) parts.Add("VBS 开");
                else parts.Add("HVCI 关");
                break;
            }
        }
        catch { /* DeviceGuard 类不存在（老系统）时省略 */ }
        return string.Join(" · ", parts);
    }

    /// <summary>显示器摘要："1920×1080 · 2 屏 · 缩放 150%"（主屏分辨率，多屏计数）</summary>
    public static string GetMonitorInfo()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0) return "";
            var main = System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
            double scale = GetDpiForSystem() / 96.0 * 100;   // Win10 1607+，线程安全
            var size = main.Bounds;
            return $"{size.Width}×{size.Height}{(screens.Length > 1 ? $" · {screens.Length} 屏" : "")} · 缩放 {scale:0}%";
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>物理盘静态/健康信息（DeviceId "0"/"1" → 标签串），如 "SSD·NVMe · 健康 98% · 3210h"</summary>
    public static Dictionary<string, string> GetDiskHealth()
    {
        var media = new Dictionary<string, (string media, string bus)>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var id = o["DeviceId"]?.ToString();
                if (id == null) continue;
                var m = Convert.ToInt32(o["MediaType"]) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                var b = Convert.ToInt32(o["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 8 => "RAID", 7 => "USB", _ => "" };
                media[id] = (m, b);
            }
        }
        catch { }

        var result = new Dictionary<string, string>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var id = o["DeviceId"]?.ToString();
                if (id == null) continue;
                var parts = new List<string>();
                if (media.TryGetValue(id, out var mb))
                {
                    var tag = string.Join("·", new[] { mb.media, mb.bus }.Where(x => x.Length > 0));
                    if (tag.Length > 0) parts.Add(tag);
                }
                if (o["Wear"] is int wear && wear >= 0 && wear <= 100)
                    parts.Add($"健康 {100 - wear}%");          // Wear=已磨损百分比
                if (o["PowerOnHours"] is int h && h > 0)
                    parts.Add(h >= 10000 ? $"{h / 1000.0:0.#}万h" : $"{h}h");
                if (parts.Count > 0) result[id] = string.Join(" · ", parts);
            }
        }
        catch { }
        return result;
    }

    // ============ 显示器 / 网卡 / 显卡驱动（详细信息，后台采集一次） ============

    /// <summary>显示器列表：型号/尺寸/接口来自 WMI EDID，分辨率/主屏/缩放来自 Win32 API。
    /// 匹配方式：EnumDisplayDevices 的 monitor DeviceID（"MONITOR\A\B"）对应 WMI InstanceName（"DISPLAY\A\B"）</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        // ① WMI：型号（WmiMonitorID）、尺寸（BasicDisplayParams）、接口类型（ConnectionParams）
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inches = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var conns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var inst = o["InstanceName"]?.ToString();
                if (!string.IsNullOrEmpty(inst)) names[inst] = DecodeEdidString(o["UserFriendlyName"]);
            }
        }
        catch { /* root\wmi 不可读时留空 */ }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var inst = o["InstanceName"]?.ToString();
                if (string.IsNullOrEmpty(inst)) continue;
                var w = Convert.ToDouble(o["MaxHorizontalImageSize"]);   // 厘米
                var h = Convert.ToDouble(o["MaxVerticalImageSize"]);
                if (w > 1 && h > 1) inches[inst] = Math.Sqrt(w * w + h * h) / 2.54;
            }
        }
        catch { }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var inst = o["InstanceName"]?.ToString();
                if (string.IsNullOrEmpty(inst)) continue;
                conns[inst] = MapOutputTechnology(Convert.ToUInt32(o["VideoOutputTechnology"]));
            }
        }
        catch { }

        // ② 枚举活动显示器（适配器 → 显示器 → 当前模式 / 主屏 / 每屏缩放）
        var result = new List<MonitorInfo>();
        for (uint i = 0; i < 16; i++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & 1 /* DISPLAY_DEVICE_ATTACHED_TO_DESKTOP */) == 0) continue;

            var mon = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevices(adapter.DeviceName, 0, ref mon, 0)) continue;

            var info = new MonitorInfo();
            var devId = mon.DeviceID;
            if (devId.StartsWith("MONITOR\\", StringComparison.OrdinalIgnoreCase))
            {
                var inst = "DISPLAY\\" + devId[8..];
                if (names.TryGetValue(inst, out var n) && n.Length > 0) info.Name = n;
                if (inches.TryGetValue(inst, out var inch)) info.Inches = inch;
                if (conns.TryGetValue(inst, out var c)) info.Connection = c;
            }

            var dm = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettings(adapter.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm))
            {
                info.Width = dm.dmPelsWidth;
                info.Height = dm.dmPelsHeight;
                info.Hz = dm.dmDisplayFrequency;
            }

            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(
                sc => string.Equals(sc.DeviceName, adapter.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (screen != null)
            {
                info.IsPrimary = screen.Primary;
                try
                {
                    // 以屏幕中心点取 HMONITOR → 每屏有效 DPI（PerMonitorV2 进程下为真实值；shcore，Win8.1+）
                    var center = new POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
                    var hMon = MonitorFromPoint(center, 1 /* MONITOR_DEFAULTTONEAREST */);
                    if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0 && dx > 0)
                        info.ScalePct = dx / 96.0 * 100;
                }
                catch { /* 旧系统无 shcore */ }
            }

            result.Add(info);
        }
        return result;
    }

    /// <summary>EDID 字符串（ushort 数组，遇 0 截断）解码</summary>
    private static string DecodeEdidString(object raw)
    {
        if (raw is not ushort[] chars || chars.Length == 0) return "";
        var sb = new System.Text.StringBuilder(chars.Length);
        foreach (var c in chars)
        {
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>D3DKMDT_VOT 枚举 → 接口名</summary>
    private static string MapOutputTechnology(uint t)
    {
        if ((t & 0x80000000u) != 0) return "内屏";   // 内部面板位（笔记本/平板）
        return t switch
        {
            0 => "VGA", 1 => "S-Video", 2 => "复合视频", 3 => "分量视频",
            4 => "DVI", 5 => "HDMI", 6 => "内屏",       // 6 = LVDS（笔记本内屏）
            10 or 11 => "DisplayPort", 12 => "UDI", 15 => "Miracast",
            _ => ""
        };
    }

    // 显示设备/模式 P/Invoke（仅本类使用）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DEVMODEW devMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>物理网卡列表（过滤虚拟设备：Hyper-V / VMware / TAP / 蓝牙 / Wi-Fi Direct 等）</summary>
    private static readonly string[] VirtualNicKeywords =
    {
        "virtual", "vmware", "virtualbox", "hyper-v", "bluetooth", "蓝牙",
        "tap-", "wan miniport", "wi-fi direct", "km-test", "vpn"
    };

    /// <summary>网卡信息：有线/无线物理网卡，含 IP、MAC、链路速率与连接状态</summary>
    public static List<NicInfo> GetNetworkAdapters()
    {
        var result = new List<NicInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                bool wired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet;
                if (!wireless && !wired) continue;

                var lower = nic.Description.ToLowerInvariant();
                if (VirtualNicKeywords.Any(k => lower.Contains(k))) continue;

                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                var mac = bytes.Length == 6 ? BitConverter.ToString(bytes) : "";
                if (mac == "00-00-00-00-00-00") continue;

                result.Add(new NicInfo
                {
                    Wireless = wireless,
                    Name = CleanNicName(nic.Description),
                    Mac = mac,
                    Up = nic.OperationalStatus == OperationalStatus.Up,
                    Ip = FirstIp(nic),
                    Speed = nic.OperationalStatus == OperationalStatus.Up && nic.Speed > 1_000_000
                        ? FormatLinkSpeed(nic.Speed)
                        : ""
                });
            }
        }
        catch { /* 枚举失败时留空 */ }
        return result;
    }

    private static string CleanNicName(string desc) =>
        desc.Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "").Trim();

    /// <summary>首个 IP：IPv4 优先，无则全局 IPv6（跳过 fe80 链路本地）</summary>
    private static string FirstIp(NetworkInterface nic)
    {
        try
        {
            var addrs = nic.GetIPProperties().UnicastAddresses;
            var v4 = addrs.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            if (v4 != null) return v4;
            return addrs.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                    && !a.Address.IsIPv6LinkLocal)?.Address.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string FormatLinkSpeed(long bps) => bps switch
    {
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.#} Gbps",
        >= 1_000_000 => $"{bps / 1_000_000:0} Mbps",
        _ => ""
    };

    /// <summary>显卡驱动版本（注册表显示类，与 GetGpuNames 同一路径）。
    /// NVIDIA 五段式版本（如 "32.0.15.6109"）转换为用户熟悉的 "561.09"</summary>
    public static List<GpuDriverInfo> GetGpuDrivers()
    {
        var result = new List<GpuDriverInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return result;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(sub, @"^\d{4}$")) continue;
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;

                result.Add(new GpuDriverInfo
                {
                    Name = desc.Trim(),
                    Version = PrettyDriverVersion(desc, k?.GetValue("DriverVersion") as string ?? ""),
                    Date = PrettyDriverDate(k?.GetValue("DriverDate") as string ?? "")
                });
            }
        }
        catch { /* 注册表不可读时留空 */ }
        return result;
    }

    /// <summary>NVIDIA 驱动版本美化："32.0.15.6109" → "561.09"（末两段合并取末 5 位，前三位.后两位）</summary>
    private static string PrettyDriverVersion(string desc, string ver)
    {
        if (string.IsNullOrEmpty(ver)) return "";
        if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            var parts = ver.Split('.');
            if (parts.Length >= 2)
            {
                var s = parts[^2] + parts[^1];
                if (s.Length >= 5 && long.TryParse(s, out _))
                    return $"{s[^5..^2]}.{s[^2..]}";
            }
        }
        return ver;
    }

    /// <summary>驱动日期 "2025-7-29" → "2025-07"</summary>
    private static string PrettyDriverDate(string date)
    {
        if (string.IsNullOrEmpty(date)) return "";
        var p = date.Split('-');
        if (p.Length == 3 && int.TryParse(p[0], out var y) && int.TryParse(p[1], out var m) && y > 1990)
            return $"{y:0000}-{m:00}";
        return date;
    }
}
