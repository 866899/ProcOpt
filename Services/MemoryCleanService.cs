using System.Diagnostics;

namespace ProcOpt.Services;

/// <summary>内存清理结果</summary>
public class CleanResult
{
    public bool Success;
    public int BeforePct;          // 清理前物理内存占用 %
    public int AfterPct;           // 清理后 %
    public double FreedMb;         // 释放的物理内存
    public int CleanedProcesses;   // 成功清理的进程数
    public int SkippedProcesses;   // 跳过（受保护等）的进程数
    public bool StandbyPurged;     // 是否清理了备用列表
    public int ElapsedMs;
    public string Error;
}

/// <summary>
/// 内存清理服务：
/// 1. 进程工作集清理（K32EmptyWorkingSet，物理页换出到页面文件）
/// 2. 系统备用列表清理（NtSetSystemInformation MemoryPurgeStandbyList，需 SeProfileSingleProcessPrivilege）
/// 3. 阈值自动清理监测（后台定时器，超过阈值且满足间隔时触发）
/// </summary>
public class MemoryCleanService
{
    private readonly System.Timers.Timer _autoTimer;
    private DateTime _lastClean = DateTime.MinValue;
    private int _cleaning;                     // Interlocked 防重入
    private volatile bool _autoEnabled;
    private int _thresholdPct = 85;
    private int _intervalMin = 10;

    /// <summary>自动清理触发时通知（在后台线程回调）</summary>
    public event Action<CleanResult> AutoCleaned;

    public MemoryCleanService()
    {
        _autoTimer = new System.Timers.Timer(30_000) { AutoReset = true };  // 每 30s 检查
        _autoTimer.Elapsed += (s, e) => CheckAutoClean();
    }

    /// <summary>配置自动清理（设置变化时调用）</summary>
    public void ConfigureAuto(bool enabled, int thresholdPct, int intervalMin)
    {
        _autoEnabled = enabled;
        _thresholdPct = Math.Clamp(thresholdPct, 50, 99);
        _intervalMin = Math.Clamp(intervalMin, 1, 720);
        if (enabled) _autoTimer.Start();
        else _autoTimer.Stop();
    }

    public void Stop() => _autoTimer.Stop();

    private void CheckAutoClean()
    {
        if (!_autoEnabled || Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0) return;
        try
        {
            var s = NativeMethods.GetMemoryStatus();
            if (s.MemoryLoad >= _thresholdPct &&
                (DateTime.Now - _lastClean).TotalMinutes >= _intervalMin)
            {
                var result = CleanCore("自动");
                if (result.Success) AutoCleaned?.Invoke(result);
            }
        }
        catch { /* 静默 */ }
        finally { Interlocked.Exchange(ref _cleaning, 0); }
    }

    /// <summary>手动一键清理：全部进程工作集 + 系统备用列表</summary>
    public CleanResult CleanNow(string source = "手动")
    {
        if (Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0)
            return new CleanResult { Error = "正在清理中" };
        try { return CleanCore(source); }
        finally { Interlocked.Exchange(ref _cleaning, 0); }
    }

    private CleanResult CleanCore(string source)
    {
        var sw = Stopwatch.StartNew();
        var before = NativeMethods.GetMemoryStatus();
        var r = new CleanResult { BeforePct = (int)before.MemoryLoad };

        // 1. 全部进程工作集（排除自身）
        (r.CleanedProcesses, r.SkippedProcesses) = EmptyAllWorkingSets();

        // 2. 系统备用列表
        r.StandbyPurged = PurgeStandbyList();

        // 等待页面换出生效后统计
        Thread.Sleep(1500);
        var after = NativeMethods.GetMemoryStatus();
        r.AfterPct = (int)after.MemoryLoad;
        r.FreedMb = (after.AvailPhys - before.AvailPhys) / 1024.0 / 1024.0;
        r.ElapsedMs = (int)sw.ElapsedMilliseconds;
        r.Success = r.CleanedProcesses > 0 || r.StandbyPurged;
        _lastClean = DateTime.Now;

        App.LogVm.Add(new Models.ApplyLogEntry
        {
            Source = source,
            ProcessName = "系统内存",
            Action = $"工作集×{r.CleanedProcesses}" + (r.StandbyPurged ? " + 备用列表" : ""),
            Success = r.Success,
            Message = $"{r.BeforePct}% → {r.AfterPct}%，释放 {r.FreedMb:F0} MB"
        });
        return r;
    }

    /// <summary>清理单个进程工作集。返回释放前的工作集大小（MB），失败抛异常。</summary>
    public double CleanProcess(int pid)
    {
        IntPtr h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) throw new InvalidOperationException("无法访问该进程（权限不足或已退出）");
        try
        {
            long beforeWs = 0;
            try { using var p = Process.GetProcessById(pid); beforeWs = p.WorkingSet64; } catch { }
            if (!NativeMethods.K32EmptyWorkingSet(h))
                throw new System.ComponentModel.Win32Exception();
            return beforeWs / 1024.0 / 1024.0;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>遍历清理全部进程工作集，返回 (成功数, 跳过数)</summary>
    private static (int cleaned, int skipped) EmptyAllWorkingSets()
    {
        int cleaned = 0, skipped = 0;
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self) continue;
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = NativeMethods.OpenProcess(
                        NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (h != IntPtr.Zero && NativeMethods.K32EmptyWorkingSet(h)) cleaned++;
                    else skipped++;
                }
                catch { skipped++; }
                finally { if (h != IntPtr.Zero) NativeMethods.CloseHandle(h); }
            }
        }
        return (cleaned, skipped);
    }

    /// <summary>清空系统备用列表（需 SeProfileSingleProcessPrivilege 特权）</summary>
    public static bool PurgeStandbyList()
    {
        try
        {
            if (!NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege"))
                return false;
            int cmd = NativeMethods.MemoryPurgeStandbyList;
            return NativeMethods.NtSetSystemInformation(
                NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;  // STATUS_SUCCESS
        }
        catch { return false; }
    }
}
