using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProcOpt.Models;

namespace ProcOpt.Services;

/// <summary>进程枚举与设置（优先级 / 相关性 / 能效模式）</summary>
public static class ProcessService
{
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new();
    private static readonly ConcurrentDictionary<int, double> LastCpuMs = new();
    private static readonly ConcurrentDictionary<int, DateTime> LastSeen = new();
    private static ImageSource _defaultIcon;
    private static readonly object _defIconLock = new();

    public static ImageSource DefaultIcon
    {
        get
        {
            lock (_defIconLock)
            {
                if (_defaultIcon == null)
                {
                    using var ico = SystemIcons.Application;
                    _defaultIcon = FromIcon(ico);
                }
                return _defaultIcon;
            }
        }
    }

    private static ImageSource FromIcon(Icon ico)
    {
        var img = Imaging.CreateBitmapSourceFromHIcon(ico.Handle,
            System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        img.Freeze();
        return img;
    }

    public static ImageSource GetIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return DefaultIcon;
        return IconCache.GetOrAdd(path, p =>
        {
            try
            {
                using var ico = Icon.ExtractAssociatedIcon(p);
                return ico == null ? DefaultIcon : FromIcon(ico);
            }
            catch { return DefaultIcon; }
        });
    }

    /// <summary>采集进程快照（请在后台线程调用）</summary>
    public static List<ProcessSnapshot> Snapshot()
    {
        var list = new List<ProcessSnapshot>();
        var procs = Process.GetProcesses();
        foreach (var p in procs)
        {
            var snap = new ProcessSnapshot { Pid = p.Id, Name = p.ProcessName };
            try { snap.Priority = p.PriorityClass; } catch { }
            try { snap.AffinityMask = (long)p.ProcessorAffinity; } catch { }
            try { snap.WorkingSet = p.WorkingSet64; } catch { }
            try { snap.CpuMs = p.TotalProcessorTime.TotalMilliseconds; } catch { }
            try { snap.Path = p.MainModule?.FileName; } catch { }
            snap.EcoOn = NativeMethods.EcoMode(p.Id, null);
            list.Add(snap);
            p.Dispose();
        }
        return list;
    }

    /// <summary>基于上次快照计算 CPU 占用（0~100，按全部逻辑核心归一化）</summary>
    public static double CalcCpu(int pid, double cpuMs, out bool isNew)
    {
        isNew = !LastCpuMs.ContainsKey(pid) || !LastSeen.ContainsKey(pid);
        var now = DateTime.UtcNow;
        if (isNew)
        {
            LastCpuMs[pid] = cpuMs;
            LastSeen[pid] = now;
            return 0;
        }
        var last = LastCpuMs[pid];
        var lastTime = LastSeen[pid];
        var elapsed = (now - lastTime).TotalMilliseconds;
        var result = elapsed <= 0 ? 0 : Math.Min(100, (cpuMs - last) / elapsed / CpuTopology.LogicalCount * 100);
        LastCpuMs[pid] = cpuMs;
        LastSeen[pid] = now;
        return result;
    }

    public static void SetPriority(int pid, ProcessPriorityClass priority)
    {
        using var p = Process.GetProcessById(pid);
        p.PriorityClass = priority;
    }

    public static void SetAffinity(int pid, long mask)
    {
        using var p = Process.GetProcessById(pid);
        p.ProcessorAffinity = (IntPtr)mask;
    }

    public static void SetEco(int pid, bool enable)
    {
        var r = NativeMethods.EcoMode(pid, enable);
        if (r == null) throw new InvalidOperationException("无法访问该进程（权限不足或进程已退出）");
    }

    /// <summary>获取进程可执行文件路径（可能失败）</summary>
    public static string TryGetPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }
}
