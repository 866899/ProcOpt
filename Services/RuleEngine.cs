using System.Diagnostics;
using System.Windows.Threading;
using ProcOpt.Models;
using ProcOpt.ViewModels;

namespace ProcOpt.Services;

/// <summary>自动应用引擎：轮询检测新进程，匹配规则并应用</summary>
public class RuleEngine
{
    private readonly RulesStore _store;
    private readonly LogViewModel _log;
    private readonly SettingsService _settings;
    private readonly HashSet<int> _seen = new();
    private DispatcherTimer _timer;
    private bool _running;

    public RuleEngine(RulesStore store, LogViewModel log, SettingsService settings)
    {
        _store = store;
        _log = log;
        _settings = settings;
    }

    public void Start()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(_settings.Settings.PollIntervalMs) };
            _timer.Tick += (s, e) => Tick();
        }
        _running = _settings.Settings.AutoApply;
        if (_running)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(_settings.Settings.PollIntervalMs);
            _timer.Start();
        }
    }

    /// <summary>设置变化后同步运行状态</summary>
    public void SyncState()
    {
        if (_settings.Settings.AutoApply)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(_settings.Settings.PollIntervalMs);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Stop() => _timer?.Stop();

    private async void Tick()
    {
        // 防重入：间隔内未完成则跳过本轮
        if (_tickBusy) return;
        _tickBusy = true;
        try
        {
            var current = new Dictionary<int, string>();
            foreach (var p in Process.GetProcesses())
                current[p.Id] = p.ProcessName;

            var newPids = current.Keys.Where(id => !_seen.Contains(id)).ToList();
            foreach (var id in current.Keys) _seen.Add(id);
            _seen.RemoveWhere(id => !current.ContainsKey(id));

            if (newPids.Count == 0) return;

            var applied = new List<(string name, int pid)>();
            await Task.Run(() =>
            {
                foreach (var pid in newPids)
                {
                    var name = current[pid];
                    var rule = _store.Match(name);
                    if (rule == null) continue;
                    var ok = ApplyRule(rule, pid, "自动应用");
                    if (ok) applied.Add((name, pid));
                }
            });

            if (applied.Count > 0 && _settings.Settings.NotifyOnApply && App.Tray != null)
            {
                var names = string.Join("、", applied.Select(a => a.name).Distinct().Take(3));
                var more = applied.Select(a => a.name).Distinct().Count() > 3 ? " 等" : "";
                App.Tray.ShowBalloon("ProcOpt 已自动应用规则", $"{names}{more} 共 {applied.Count} 个进程");
            }
        }
        catch { /* 单轮失败静默跳过 */ }
        finally { _tickBusy = false; }
    }

    private bool _tickBusy;

    /// <summary>把规则应用到指定 PID，记录日志。返回是否全部成功。</summary>
    public bool ApplyRule(ProcessRule rule, int pid, string source)
    {
        var actions = new List<string>();
        var errors = new List<string>();

        if (rule.SetPriority)
            TryDo(actions, errors, $"优先级→{ProcessItemView.ToText(rule.Priority)}",
                () => ProcessService.SetPriority(pid, rule.Priority));
        if (rule.SetEco)
            TryDo(actions, errors, $"能效→{(rule.EcoEnabled ? "效率模式" : "标准")}",
                () => ProcessService.SetEco(pid, rule.EcoEnabled));
        if (rule.SetAffinity)
            TryDo(actions, errors, $"相关性→{ProcessItemView.MaskToText(rule.AffinityMask)}",
                () => ProcessService.SetAffinity(pid, rule.AffinityMask));

        if (actions.Count == 0 && errors.Count == 0) return false;

        string procName = rule.ProcessName;
        try { var p = Process.GetProcessById(pid); procName = p.ProcessName; } catch { }

        _log.Add(new ApplyLogEntry
        {
            Source = source,
            ProcessName = procName,
            Pid = pid,
            Action = string.Join("; ", actions.Concat(errors.Count > 0 ? new[] { "部分失败" } : Array.Empty<string>())),
            Success = errors.Count == 0,
            Message = string.Join("; ", errors)
        });
        return errors.Count == 0;
    }

    /// <summary>立即将规则应用到当前运行的全部同名进程</summary>
    public int ApplyToRunning(ProcessRule rule, string source = "立即应用")
    {
        int count = 0;
        foreach (var p in Process.GetProcesses())
        {
            if (ProcessRule.NormalizeName(p.ProcessName) == ProcessRule.NormalizeName(rule.ProcessName))
            {
                if (ApplyRule(rule, p.Id, source)) count++;
            }
            p.Dispose();
        }
        return count;
    }

    private static void TryDo(List<string> actions, List<string> errors, string text, Action act)
    {
        try
        {
            act();
            actions.Add(text);
        }
        catch (Exception ex)
        {
            errors.Add($"{text} 失败: {ex.Message}");
        }
    }
}
