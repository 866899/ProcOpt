using System.Diagnostics;

namespace ProcOpt.Services;

/// <summary>开机自启动（任务计划程序，以最高权限运行——因为本程序需要管理员）</summary>
public static class AutoStartService
{
    private const string TaskName = "ProcOptAutoStart";

    private static string ExePath => Environment.ProcessPath
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ProcOpt.exe");

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(output) ? $"schtasks 退出码 {p.ExitCode}" : output);
    }

    public static string Enable()
    {
        // 登录时以最高权限静默启动：--tray 参数让程序仅驻留托盘、不弹窗口
        return Run($"/Create /TN {TaskName} /TR \"\\\"{ExePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F");
    }

    public static string Disable() => Run($"/Delete /TN {TaskName} /F");

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN {TaskName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
