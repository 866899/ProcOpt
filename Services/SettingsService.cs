using System.IO;
using System.Text.Json;
using ProcOpt.Models;

namespace ProcOpt.Services;

public class SettingsService
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcOpt");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Settings { get; } = new();

    public void Load()
    {
        try
        {
            var path = Path.Combine(DataDir, "settings.json");
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    loaded.CopyInto(Settings);
                }
            }
        }
        catch { /* 配置损坏时使用默认值 */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "settings.json"),
                JsonSerializer.Serialize(Settings, JsonOpts));
        }
        catch { /* 忽略写入失败 */ }
    }
}

file static class Extensions
{
    public static void CopyInto(this AppSettings src, AppSettings dst)
    {
        dst.AutoApply = src.AutoApply;
        dst.PollIntervalMs = src.PollIntervalMs;
        dst.NotifyOnApply = src.NotifyOnApply;
        dst.StartWithWindows = src.StartWithWindows;
        dst.MinimizeToTray = src.MinimizeToTray;
    }
}
