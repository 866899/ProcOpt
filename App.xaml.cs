using System.IO;
using System.Threading;
using System.Windows;
using ProcOpt.Models;
using ProcOpt.Services;
using ProcOpt.ViewModels;
using ProcOpt.Views;

namespace ProcOpt;

public partial class App : Application
{
    static App()
    {
        // 单文件发布修复：WPF 打开 Popup/ContextMenu（右键菜单）时，内部会按 .NET Framework
        // 时代的强名称（Version=4.0.0.0）解析 Accessibility 程序集，而单文件 bundle 中只有
        // 当前版本（8.0.0.0），强名称解析失败抛 FileNotFoundException。
        // 提前按简单名加载后，后续同名解析直接命中已加载实例；AssemblyResolve 再兜底一层。
        try { _ = System.Reflection.Assembly.Load("Accessibility"); } catch { }
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            if (e.Name.StartsWith("Accessibility,", StringComparison.OrdinalIgnoreCase))
            {
                try { return System.Reflection.Assembly.Load("Accessibility"); } catch { }
            }
            return null;
        };
    }

    private static Mutex _mutex;
    private static EventWaitHandle _showEvent;

    /// <summary>跨进程"显示主窗口"通知事件名</summary>
    private const string ShowWindowEventName = "ProcOpt_ShowWindow";

    /// <summary>正在退出时为 true，窗口 Closing 据此放行关闭</summary>
    public static bool IsExiting { get; private set; }

    public static SettingsService SettingsSvc { get; private set; }
    public static RulesStore Rules { get; private set; }
    public static LogViewModel LogVm { get; private set; }
    public static RuleEngine Engine { get; private set; }
    public static TrayIconService Tray { get; private set; }
    public static MainWindow MainWin { get; private set; }
    public static ViewModels.SettingsViewModel SettingsVm { get; private set; }
    public static Services.MemoryCleanService MemClean { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 开机自启任务携带 --tray：静默启动，仅托盘驻留
        bool silentStart = e.Args.Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/tray", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, "ProcOpt_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：通知其显示主窗口，本实例静默退出（不弹提示）
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowWindowEventName);
                evt.Set();
            }
            catch { /* 主实例刚启动尚未就绪等极端情况：放弃通知，直接退出 */ }
            Shutdown();
            return;
        }

        // 监听后续实例的"显示主窗口"请求
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var listener = new Thread(WatchShowRequests) { IsBackground = true };
        listener.Start();

        DispatcherUnhandledException += (s, args) =>
        {
            LogException(args.Exception, "UI线程");
            ShowError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            LogException(args.ExceptionObject as Exception, "后台线程");
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogException(args.Exception, "Task");
            args.SetObserved();
        };

        // 初始化目录与配置
        Directory.CreateDirectory(SettingsService.DataDir);
        SettingsSvc = new SettingsService();
        SettingsSvc.Load();
        Rules = new RulesStore();
        Rules.Load();
        LogVm = new LogViewModel();

        Engine = new RuleEngine(Rules, LogVm, SettingsSvc);
        Tray = new TrayIconService();
        SettingsVm = new ViewModels.SettingsViewModel();

        // 内存清理：托盘一键 + 阈值自动监测
        MemClean = new Services.MemoryCleanService();
        MemClean.AutoCleaned += r => Current.Dispatcher.Invoke(() =>
            Tray?.ShowBalloon("内存自动清理完成",
                $"{r.BeforePct}% → {r.AfterPct}%，释放 {r.FreedMb:F0} MB（清理 {r.CleanedProcesses} 个进程）"));
        MemClean.ConfigureAuto(
            SettingsSvc.Settings.AutoCleanMemory,
            SettingsSvc.Settings.AutoCleanThresholdPct,
            SettingsSvc.Settings.AutoCleanIntervalMin);

        // 主窗口初始化失败时记录并退出，避免留下占用单实例锁的僵尸进程
        try
        {
            MainWin = new MainWindow();
        }
        catch (Exception ex)
        {
            LogException(ex, "启动失败");
            MessageBox.Show($"程序初始化失败，即将退出：\n\n{ex.Message}", "ProcOpt",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
            return;
        }

        Tray.Init(
            openWindow: () => ShowMainWindow(),
            isAutoApplyOn: () => SettingsSvc.Settings.AutoApply,
            setAutoApply: v =>
            {
                SettingsSvc.Settings.AutoApply = v;
                SettingsSvc.Save();
                Engine.SyncState();
                SettingsVm?.RefreshAutoApply();
            },
            cleanMemory: () => _ = Task.Run(() =>
            {
                var r = MemClean.CleanNow("托盘");
                Current.Dispatcher.Invoke(() =>
                    Tray?.ShowBalloon("内存清理完成",
                        r.Success ? $"{r.BeforePct}% → {r.AfterPct}%，释放 {r.FreedMb:F0} MB"
                                  : $"清理失败：{r.Error}"));
            }),
            exit: ExitApp);

        Engine.Start();
        if (silentStart)
        {
            // 静默启动：仅托盘驻留，不显示窗口
            Tray.ShowBalloon("ProcOpt 正在后台运行", "规则将自动应用，双击托盘图标可打开主窗口");
        }
        else
        {
            MainWin.Show();
        }

        // 若开机自启任务为旧定义（未带 --tray 参数），静默刷新任务
        if (SettingsSvc.Settings.StartWithWindows)
            _ = Task.Run(() => AutoStartService.Enable());

        // 启动后延迟静默检查新版本，发现新版时托盘气泡提示（用户主动到设置页安装）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8)); // 避开启动高峰
                var (info, _) = await Services.UpdateService.CheckAsync();
                if (Services.UpdateService.HasNewer(info))
                {
                    Current.Dispatcher.Invoke(() =>
                        Tray?.ShowBalloon("发现新版本 " + info.Tag,
                            $"当前 {Services.UpdateService.CurrentVersion.ToString(3)} → {info.Version}，请到 设置 页面检查更新"));
                }
            }
            catch { /* 静默失败 */ }
        });

        base.OnStartup(e);
    }

    /// <summary>后台线程：等待后续实例的显示请求，唤醒主窗口</summary>
    private static void WatchShowRequests()
    {
        try
        {
            while (true)
            {
                _showEvent.WaitOne();
                Current?.Dispatcher.Invoke(() => ShowMainWindow());
            }
        }
        catch { /* 应用退出时线程随进程终止 */ }
    }

    public static void ShowMainWindow()
    {
        // 窗口可能已被用户关闭（未启用最小化到托盘时），此时重建
        MainWin ??= new MainWindow();
        MainWin.Show();
        MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    /// <summary>主窗口真正关闭后调用，置空引用以便下次从托盘重建</summary>
    internal static void NotifyWindowClosed() => MainWin = null;

    public static void ExitApp()
    {
        IsExiting = true;
        Engine?.Stop();
        MemClean?.Stop();
        Tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        Current.Shutdown();
    }

    private static void LogException(Exception ex, string source)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.AppendAllText(
                Path.Combine(SettingsService.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    private static void ShowError(Exception ex)
    {
        var text = ex.ToString();
        if (text.Length > 1500) text = text[..1500] + "\r\n……";
        MessageBox.Show(
            $"发生未处理的错误：\n\n{text}\n\n完整信息已记录到 %APPDATA%\\ProcOpt\\error.log",
            "ProcOpt", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
