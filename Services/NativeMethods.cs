using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProcOpt.Services;

internal static class NativeMethods
{
    // ---------- 能效模式（EcoQoS / Power Throttling，Win11 任务管理器"效率模式"同款 API） ----------
    internal const int ProcessPowerThrottling = 4;
    internal const uint PowerThrottlingCurrentVersion = 1;
    internal const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetProcessInformation(IntPtr hProcess, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessInformation(IntPtr hProcess, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    // ---------- 进程访问 ----------
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>查询/设置能效模式。enable=null 表示仅查询。</summary>
    internal static bool? EcoMode(int pid, bool? enable)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = enable == null ? 0 : PowerThrottlingExecutionSpeed,
                StateMask = enable == true ? PowerThrottlingExecutionSpeed : 0
            };
            if (enable != null)
            {
                if (!SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                    throw new Win32Exception();
            }
            var q = new PROCESS_POWER_THROTTLING_STATE { Version = PowerThrottlingCurrentVersion };
            if (!GetProcessInformation(h, ProcessPowerThrottling, ref q, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return null;
            return (q.StateMask & PowerThrottlingExecutionSpeed) != 0;
        }
        finally { CloseHandle(h); }
    }

    // ---------- CPU 拓扑（大小核识别） ----------
    internal const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    // ---------- DWM（深色标题栏 / Mica） ----------
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // ---------- 系统内存 ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;          // 物理内存占用百分比
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>获取物理内存占用（百分比与字节）</summary>
    internal static MEMORYSTATUSEX GetMemoryStatus()
    {
        var s = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref s);
        return s;
    }

    // ---------- CPU 占用采样（每核心，比 PerformanceCounter 快且无初始化开销） ----------
    internal const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;       // 100ns 单位
        public long KernelTime;     // 含 Idle
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    internal static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>取每个逻辑处理器的时间片。失败返回 null。</summary>
    internal static SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] QueryProcessorTimes()
    {
        int n = Environment.ProcessorCount;
        int size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size * n);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, size * n, out _) != 0)
                return null;
            var arr = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[n];
            for (int i = 0; i < n; i++)
                arr[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * size);
            return arr;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 内存清理 ----------
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

    /// <summary>清空进程工作集（物理内存页换出到页面文件）</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    internal static extern uint NtSetSystemInformation(int infoClass, ref int info, int length);

    /// <summary>系统内存列表信息类（备用列表清理用）</summary>
    internal const int SystemMemoryListInformation = 0x50;
    /// <summary>命令：清空备用列表（Standby List）</summary>
    internal const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool LookupPrivilegeValue(string systemName, string name, ref LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int SE_PRIVILEGE_ENABLED = 0x00000002;

    /// <summary>提升当前进程特权（如 SeProfileSingleProcessPrivilege），返回是否成功</summary>
    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = SE_PRIVILEGE_ENABLED };
            if (!LookupPrivilegeValue(null, name, ref tp.Luid)) return false;
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }
}
