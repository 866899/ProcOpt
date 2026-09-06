using System.Diagnostics;
using System.Windows.Media;
using ProcOpt.Infrastructure;

namespace ProcOpt.Models;

/// <summary>进程快照（后台线程采集的数据包）</summary>
public class ProcessSnapshot
{
    public int Pid { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public ProcessPriorityClass? Priority { get; set; }
    public long? AffinityMask { get; set; }
    public bool? EcoOn { get; set; }
    public double CpuMs { get; set; }
    public long WorkingSet { get; set; }
}

/// <summary>进程列表行模型</summary>
public class ProcessItemView : ObservableObject
{
    public int Pid { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public ImageSource Icon { get; set; }

    private ProcessPriorityClass? _priority;
    private string _priorityText = "无法读取";
    private bool? _ecoOn;
    private double _cpu;
    private double _memMb;
    private long? _affinityMask;
    private string _affinityText = "-";

    public ProcessPriorityClass? Priority { get => _priority; set { if (SetProperty(ref _priority, value)) PriorityText = ToText(value); } }
    public string PriorityText { get => _priorityText; private set => SetProperty(ref _priorityText, value); }
    public bool? EcoOn { get => _ecoOn; set => SetProperty(ref _ecoOn, value); }
    public double Cpu { get => _cpu; set => SetProperty(ref _cpu, value); }
    public double MemMb { get => _memMb; set => SetProperty(ref _memMb, value); }
    public long? AffinityMask { get => _affinityMask; set { if (SetProperty(ref _affinityMask, value)) AffinityText = MaskToText(value); } }
    public string AffinityText { get => _affinityText; private set => SetProperty(ref _affinityText, value); }

    public string EcoText => EcoOn switch { true => "效率模式", false => "标准", _ => "-" };

    public void RaiseEcoChanged() => OnPropertyChanged(nameof(EcoText));

    public static string ToText(ProcessPriorityClass? p) => p switch
    {
        ProcessPriorityClass.RealTime => "实时",
        ProcessPriorityClass.High => "高",
        ProcessPriorityClass.AboveNormal => "高于正常",
        ProcessPriorityClass.Normal => "正常",
        ProcessPriorityClass.BelowNormal => "低于正常",
        ProcessPriorityClass.Idle => "低",
        _ => "无法读取"
    };

    /// <summary>将掩码转为 "0-7,10" 形式；全部核心时返回 "全部"</summary>
    public static string MaskToText(long? mask)
    {
        if (mask is not long m) return "-";
        if (m == Services.CpuTopology.AllMask || m == -1) return "全部";
        var parts = new List<string>();
        int start = -1;
        for (int i = 0; i < 64; i++)
        {
            bool bit = (m >> i & 1) != 0;
            if (bit && start < 0) start = i;
            else if (!bit && start >= 0)
            {
                parts.Add(i - 1 == start ? $"{start}" : $"{start}-{i - 1}");
                start = -1;
            }
        }
        if (start >= 0) parts.Add(63 == start ? $"{start}" : $"{start}-63");
        return parts.Count == 0 ? "无" : string.Join(",", parts);
    }
}
