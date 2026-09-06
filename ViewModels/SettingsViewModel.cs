using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using ProcOpt.Infrastructure;
using ProcOpt.Models;
using ProcOpt.Services;

namespace ProcOpt.ViewModels;

public class SettingsViewModel : ObservableObject
{
    public AppSettings S => App.SettingsSvc.Settings;

    public string AppVersion => $"v{UpdateService.CurrentVersion.ToString(3)}";

    // ---- 在线更新 ----
    private string _updateStatus = "";
    private bool _updateBusy;

    /// <summary>更新下载渠道选项（与 UpdateService.ProxyUrls 一一对应）</summary>
    public string[] UpdateProxyChoices => UpdateService.ProxyNames;

    public int UpdateProxyIndex
    {
        get => S.UpdateProxyIndex;
        set { S.UpdateProxyIndex = value; App.SettingsSvc.Save(); OnPropertyChanged(); }
    }

    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public bool UpdateBusy
    {
        get => _updateBusy;
        private set { SetProperty(ref _updateBusy, value); Infrastructure.RelayCommand.Refresh(); }
    }

    /// <summary>检查更新：有新版时询问并一键下载安装（下载完成自动重启替换）</summary>
    public ICommand CheckUpdateCommand => new RelayCommand(async () =>
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            var (info, err) = await UpdateService.CheckAsync();
            if (info == null)
            {
                UpdateStatus = "";
                MessageBox.Show(err ?? "检查更新失败。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!UpdateService.HasNewer(info))
            {
                UpdateStatus = $"已是最新版本 {AppVersion}";
                return;
            }

            var sizeMb = info.AssetSize > 0 ? $"（{info.AssetSize / 1024.0 / 1024:0.#} MB）" : "";
            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n更新内容：\n{info.Notes}";
            UpdateStatus = $"发现新版本 {info.Tag}";

            var choice = MessageBox.Show(
                $"发现新版本 {info.Tag}{sizeMb}，当前 {AppVersion}。{notes}\n\n现在下载并安装吗？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                UpdateStatus = $"新版本 {info.Tag} 可用，可稍后在此更新";
                return;
            }

            var progress = new Progress<double>(p =>
                UpdateStatus = $"正在下载 {p * 100:0}%");
            await UpdateService.DownloadAsync(info, progress);

            UpdateStatus = "下载完成，正在重启安装…";
            if (MessageBox.Show("下载完成，程序将重启以完成安装。", "准备安装",
                    MessageBoxButton.OK, MessageBoxImage.Information) == MessageBoxResult.OK)
            {
                UpdateService.ApplyAndRestart();
                App.ExitApp();
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = "";
            MessageBox.Show($"更新失败：{ex.Message}", "更新",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { UpdateBusy = false; }
    }, () => !UpdateBusy);

    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public string CpuSummary { get; } =
        $"{CpuTopology.LogicalCount} 线程 · {CpuTopology.PCoreCount} 个 P 核" +
        (CpuTopology.HasECore ? $" · {CpuTopology.ECoreCount} 个 E 核" : "（未检测到 E 核）");

    public string RuleCount => $"共 {App.Rules.Rules.Count} 条规则";

    // ---- 通用 ----
    public bool MinimizeToTray
    {
        get => S.MinimizeToTray;
        set { S.MinimizeToTray = value; App.SettingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            string err = value ? AutoStartService.Enable() : AutoStartService.Disable();
            if (err != null)
            {
                MessageBox.Show($"设置开机自启动失败：\n{err}", "ProcOpt",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                S.StartWithWindows = value;
                App.SettingsSvc.Save();
            }
            OnPropertyChanged(); // 失败时恢复显示
        }
    }

    // ---- 自动应用 ----
    public bool AutoApply
    {
        get => S.AutoApply;
        set
        {
            S.AutoApply = value;
            App.SettingsSvc.Save();
            App.Engine.SyncState();
            App.Tray.SyncAutoApply(value);
            OnPropertyChanged();
        }
    }

    public bool NotifyOnApply
    {
        get => S.NotifyOnApply;
        set { S.NotifyOnApply = value; App.SettingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>托盘等外部途径改变开关后刷新界面</summary>
    public void RefreshAutoApply() => OnPropertyChanged(nameof(AutoApply));

    public int PollIntervalSec
    {
        get => S.PollIntervalMs / 1000;
        set
        {
            S.PollIntervalMs = value * 1000;
            App.SettingsSvc.Save();
            App.Engine.SyncState();
            OnPropertyChanged();
        }
    }

    public int[] IntervalChoices { get; } = { 1, 2, 3, 5, 10 };

    // ---- 内存清理 ----
    public bool AutoCleanMemory
    {
        get => S.AutoCleanMemory;
        set
        {
            S.AutoCleanMemory = value;
            App.SettingsSvc.Save();
            App.MemClean?.ConfigureAuto(S.AutoCleanMemory, S.AutoCleanThresholdPct, S.AutoCleanIntervalMin);
            OnPropertyChanged();
        }
    }

    public int AutoCleanThresholdPct
    {
        get => S.AutoCleanThresholdPct;
        set
        {
            S.AutoCleanThresholdPct = value;
            App.SettingsSvc.Save();
            App.MemClean?.ConfigureAuto(S.AutoCleanMemory, S.AutoCleanThresholdPct, S.AutoCleanIntervalMin);
            OnPropertyChanged();
        }
    }

    public int[] CleanThresholdChoices { get; } = { 70, 75, 80, 85, 90, 95 };

    public int AutoCleanIntervalMin
    {
        get => S.AutoCleanIntervalMin;
        set
        {
            S.AutoCleanIntervalMin = value;
            App.SettingsSvc.Save();
            App.MemClean?.ConfigureAuto(S.AutoCleanMemory, S.AutoCleanThresholdPct, S.AutoCleanIntervalMin);
            OnPropertyChanged();
        }
    }

    public int[] CleanIntervalChoices { get; } = { 5, 10, 15, 30, 60 };

    public ICommand RefreshInfoCommand => new RelayCommand(() =>
    {
        OnPropertyChanged(nameof(RuleCount));
        OnPropertyChanged(nameof(CpuSummary));
    });
}
