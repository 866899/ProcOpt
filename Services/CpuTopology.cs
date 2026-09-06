using System.Runtime.InteropServices;

namespace ProcOpt.Services;

/// <summary>一个物理核心（可能含多个逻辑处理器/SMT）</summary>
public class CoreInfo
{
    public int Index { get; set; }                    // 物理核序号
    public byte EfficiencyClass { get; set; }         // 0=P核(性能) 1=E核(效率)
    public List<int> LogicalIndices { get; set; } = new();
    public bool IsECore => EfficiencyClass != 0;
    public string KindText => IsECore ? "E核" : "P核";
}

/// <summary>CPU 拓扑信息（P/E 核、逻辑处理器分布、组 0 掩码）</summary>
public static class CpuTopology
{
    private static List<CoreInfo> _cores;
    private static readonly object _lock = new();

    public static List<CoreInfo> Cores
    {
        get { lock (_lock) { _cores ??= Build(); return _cores; } }
    }

    public static int LogicalCount => Environment.ProcessorCount;

    public static int PCoreCount => Cores.Count(c => !c.IsECore);
    public static int ECoreCount => Cores.Count(c => c.IsECore);

    /// <summary>逻辑处理器掩码（组 0）。下标 i 的位表示逻辑处理器 i。</summary>
    public static long MaskOfLogicalIndex(int idx) => 1L << idx;

    public static long AllMask => LogicalCount >= 64 ? -1L : (1L << LogicalCount) - 1;
    public static long OnlyPMask => Cores.Where(c => !c.IsECore).Aggregate(0L, (m, c) => m | c.LogicalIndices.Aggregate(0L, (a, i) => a | MaskOfLogicalIndex(i)));
    public static long OnlyEMask => ECoreCount == 0 ? 0 : Cores.Where(c => c.IsECore).Aggregate(0L, (m, c) => m | c.LogicalIndices.Aggregate(0L, (a, i) => a | MaskOfLogicalIndex(i)));

    public static bool HasECore => ECoreCount > 0;

    private static unsafe List<CoreInfo> Build()
    {
        var result = new List<CoreInfo>();
        uint len = 0;
        NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, IntPtr.Zero, ref len);
        if (len == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, buf, ref len))
                return result;

            // 收集各记录起始指针（记录头: int Relationship@0, uint Size@4）
            var records = new List<nint>();
            byte* p = (byte*)buf;
            byte* end = p + len;
            while (p < end)
            {
                uint size = *(uint*)(p + 4);
                if (size == 0) break;
                records.Add((nint)p);
                p += size;
            }

            // 不同 Windows 版本的 PROCESSOR_RELATIONSHIP 布局不同：
            //   旧版:  GroupCount@14，GroupMasks@16
            //   新版（Win11 24H2+，实测）: GroupCount@30，GroupMasks@32
            // 依次尝试，以「逻辑处理器总数 == 系统线程数」判定命中
            foreach (int gcOff in new[] { 14, 30 })
            {
                var cores = TryParse(records, gcOff, LogicalCount, out int total);
                if (cores != null) return cores;
            }

            // 兜底：无法识别布局时按单核心列表处理（全部视作 P 核，功能可用但无分组信息）
            for (int i = 0; i < LogicalCount; i++)
                result.Add(new CoreInfo { Index = i, EfficiencyClass = 0, LogicalIndices = { i } });
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static unsafe List<CoreInfo> TryParse(List<nint> records, int gcOff, int expected, out int logicalTotal)
    {
        logicalTotal = 0;
        var cores = new List<CoreInfo>();
        foreach (var rp in records)
        {
            byte* p = (byte*)rp;
            uint size = *(uint*)(p + 4);
            byte effClass = *(p + 9);                       // EfficiencyClass: 0=P核 1=E核
            if (gcOff + 2 + 16 > size) { logicalTotal = -1; return null; }
            ushort groupCount = *(ushort*)(p + gcOff);
            if (groupCount < 1 || groupCount > 8) { logicalTotal = -1; return null; }
            if (gcOff + 2 + groupCount * 16 > size) { logicalTotal = -1; return null; }

            var core = new CoreInfo { Index = cores.Count, EfficiencyClass = effClass };
            for (int g = 0; g < groupCount; g++)
            {
                ulong mask = *(ulong*)(p + gcOff + 2 + 16 * g);      // GROUP_AFFINITY.Mask
                ushort group = *(ushort*)(p + gcOff + 2 + 16 * g + 8); // GROUP_AFFINITY.Group
                if (group != 0) continue;                             // 仅处理组 0（>64 线程场景罕见）
                for (int i = 0; i < 64; i++)
                    if ((mask >> i & 1) != 0) core.LogicalIndices.Add(i);
            }
            if (core.LogicalIndices.Count == 0) { logicalTotal = -1; return null; }
            logicalTotal += core.LogicalIndices.Count;
            if (logicalTotal > expected) return null;
            cores.Add(core);
        }
        return logicalTotal == expected ? cores : null;
    }
}
