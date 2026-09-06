using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ProcOpt.Models;

namespace ProcOpt.Services;

/// <summary>规则存储：内存集合 + JSON 持久化</summary>
public class RulesStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ObservableCollection<ProcessRule> Rules { get; } = new();

    public event Action RulesChanged;

    public void Load()
    {
        Rules.Clear();
        try
        {
            var path = Path.Combine(SettingsService.DataDir, "rules.json");
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<ProcessRule>>(File.ReadAllText(path));
                if (loaded != null)
                    foreach (var r in loaded.Where(r => r != null && r.IsValid()))
                        Rules.Add(r);
            }
        }
        catch { /* 规则文件损坏时从空开始 */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.WriteAllText(Path.Combine(SettingsService.DataDir, "rules.json"),
                JsonSerializer.Serialize(Rules.ToList(), JsonOpts));
            RulesChanged?.Invoke();
        }
        catch { }
    }

    public ProcessRule Match(string processName)
    {
        var n = ProcessRule.NormalizeName(processName);
        return Rules.FirstOrDefault(r => r.Enabled && ProcessRule.NormalizeName(r.ProcessName) == n);
    }
}
