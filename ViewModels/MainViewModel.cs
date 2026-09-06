using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ProcOpt.Infrastructure;
using ProcOpt.Models;
using ProcOpt.Services;

namespace ProcOpt.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private string _searchText = "";
    private string _statusText = "就绪";
    private int _processCount;
    private bool _autoApplyOn;

    public ObservableCollection<ProcessItemView> Processes { get; } = new();

    public ICollectionView View { get; }

    /// <summary>当前在列表中选中的进程（由页面 SelectionChanged 更新）</summary>
    public IList<ProcessItemView> SelectedProcesses { get; set; } = new List<ProcessItemView>();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                View.Refresh();
        }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public int ProcessCount { get => _processCount; set => SetProperty(ref _processCount, value); }
    public bool AutoApplyOn
    {
        get => _autoApplyOn;
        set => SetProperty(ref _autoApplyOn, value);
    }

    public MainViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Processes);
        View.Filter = o =>
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            var p = (ProcessItemView)o;
            return p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                   || p.Pid.ToString().Contains(SearchText);
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (s, e) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void ApplySettings(AppSettings s)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, s.PollIntervalMs));
        AutoApplyOn = s.AutoApply;
    }

    /// <summary>后台采集并差量更新列表</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snapshots = await Task.Run(() => ProcessService.Snapshot());
            var byPid = snapshots.ToDictionary(s => s.Pid);
            var existing = Processes.ToDictionary(p => p.Pid);

            // 移除已退出进程
            foreach (var pid in existing.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList())
                Processes.Remove(existing[pid]);

            foreach (var s in snapshots)
            {
                if (existing.TryGetValue(s.Pid, out var item))
                {
                    item.Priority = s.Priority;
                    item.AffinityMask = s.AffinityMask;
                    item.Cpu = ProcessService.CalcCpu(s.Pid, s.CpuMs, out _);
                    item.MemMb = s.WorkingSet / 1024.0 / 1024.0;
                    item.EcoOn = s.EcoOn;
                    item.RaiseEcoChanged();
                }
                else
                {
                    var icon = await Task.Run(() => ProcessService.GetIcon(s.Path));
                    var n = new ProcessItemView
                    {
                        Pid = s.Pid,
                        Name = s.Name,
                        Path = s.Path,
                        Icon = icon,
                        Priority = s.Priority,
                        AffinityMask = s.AffinityMask,
                        Cpu = ProcessService.CalcCpu(s.Pid, s.CpuMs, out _),
                        MemMb = s.WorkingSet / 1024.0 / 1024.0,
                        EcoOn = s.EcoOn
                    };
                    Processes.Add(n);
                }
            }
            ProcessCount = Processes.Count;
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally { _refreshing = false; }
    }

    public ICommand RefreshCommand => new RelayCommand(async () => await RefreshAsync());

    public ICommand ApplyPriorityCommand => new RelayCommand(param =>
    {
        if (!Enum.TryParse<ProcessPriorityClass>((string)param, out var pri)) return;
        if (pri == ProcessPriorityClass.RealTime)
        {
            var r = System.Windows.MessageBox.Show(
                "实时优先级会抢占几乎所有系统资源，可能导致系统无响应。\n确定要继续吗？",
                "警告", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (r != System.Windows.MessageBoxResult.Yes) return;
        }
        ApplyToSelected(p => ProcessService.SetPriority(p.Pid, pri),
            $"{ProcessItemView.ToText(pri)}优先级");
    });

    public ICommand ApplyEcoCommand => new RelayCommand(param =>
    {
        bool on = (string)param == "on";
        ApplyToSelected(p => ProcessService.SetEco(p.Pid, on),
            on ? "效率模式" : "标准能效");
    });

    /// <summary>对选中进程整理内存（清空工作集）</summary>
    public ICommand CleanMemoryCommand => new RelayCommand(() =>
        ApplyToSelected(p => App.MemClean.CleanProcess(p.Pid), "整理内存"));

    private void ApplyToSelected(Action<ProcessItemView> act, string what)
    {
        var targets = SelectedProcesses.Where(p => p != null).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先选择进程";
            return;
        }
        int ok = 0, fail = 0;
        string firstError = null;
        foreach (var t in targets)
        {
            try { act(t); ok++; }
            catch (Exception ex)
            {
                fail++;
                firstError ??= $"{t.Name}: {ex.Message}";
                App.LogVm.Add(new ApplyLogEntry
                {
                    Source = "手动", ProcessName = t.Name, Pid = t.Pid,
                    Action = what, Success = false, Message = ex.Message
                });
            }
        }
        StatusText = fail == 0
            ? $"已将「{what}」应用到 {ok} 个进程"
            : $"已应用 {ok} 个，失败 {fail} 个（{firstError}）";
        _ = RefreshAsync();
    }
}
