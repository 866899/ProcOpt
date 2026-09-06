namespace ProcOpt.Models;

public class ApplyLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";      // 自动应用 / 手动 / 立即应用
    public string ProcessName { get; set; } = "";
    public int Pid { get; set; }
    public string Action { get; set; } = "";      // 如 "优先级→高; 能效→开启"
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
