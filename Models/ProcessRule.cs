using System.Diagnostics;

namespace ProcOpt.Models;

/// <summary>进程规则：按进程名自动应用的预设（未启用的项不生效）</summary>
public class ProcessRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>进程名（不含 .exe，匹配时忽略大小写）</summary>
    public string ProcessName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool SetPriority { get; set; }
    public ProcessPriorityClass Priority { get; set; } = ProcessPriorityClass.Normal;

    public bool SetEco { get; set; }
    public bool EcoEnabled { get; set; } = true;

    public bool SetAffinity { get; set; }
    public long AffinityMask { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool IsValid()
        => !string.IsNullOrWhiteSpace(ProcessName)
           && (SetPriority || SetEco || SetAffinity)
           && (!SetAffinity || AffinityMask != 0);

    public ProcessRule Clone() => (ProcessRule)MemberwiseClone();

    // ---- 显示用（不参与序列化） ----
    [System.Text.Json.Serialization.JsonIgnore]
    public string PriorityDisplay => SetPriority ? PriorityText(Priority) : "—";

    [System.Text.Json.Serialization.JsonIgnore]
    public string EcoDisplay => !SetEco ? "—" : EcoEnabled ? "效率模式" : "标准";

    [System.Text.Json.Serialization.JsonIgnore]
    public string AffinityDisplay => SetAffinity ? ProcessItemView.MaskToText(AffinityMask) : "—";

    [System.Text.Json.Serialization.JsonIgnore]
    public string CreatedDisplay => CreatedAt.ToString("MM-dd HH:mm");

    private static string PriorityText(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.RealTime => "实时",
        ProcessPriorityClass.High => "高",
        ProcessPriorityClass.AboveNormal => "高于正常",
        ProcessPriorityClass.Normal => "正常",
        ProcessPriorityClass.BelowNormal => "低于正常",
        ProcessPriorityClass.Idle => "低",
        _ => "正常"
    };

    public static string NormalizeName(string name)
        => (name ?? "").Trim().ToLowerInvariant().Replace(".exe", "");
}
