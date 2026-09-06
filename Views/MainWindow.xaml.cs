using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProcOpt.Models;
using ProcOpt.Services;
using ProcOpt.ViewModels;
using ProcOpt.Views.Pages;

namespace ProcOpt.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainVm = new();
    private readonly RulesViewModel _rulesVm = new();
    private readonly LogViewModel _logVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly SystemViewModel _systemVm = new();

    private readonly ProcessPage _processPage;
    private readonly RulesPage _rulesPage;
    private readonly LogPage _logPage;
    private readonly SettingsPage _settingsPage;
    private readonly SystemPage _systemPage;

    private bool _isAdmin;
    private DispatcherTimer _statusTimer;

    /// <summary>内存图表：保留的采样数（1 秒/次 ≈ 最近 1 分钟）</summary>
    private const int MemMaxSamples = 60;
    private readonly Queue<double> _memSamples = new();

    public MainWindow()
    {
        InitializeComponent();
        _logVm = App.LogVm;
        _settingsVm = App.SettingsVm;

        _processPage = new ProcessPage(_mainVm);
        _rulesPage = new RulesPage(_rulesVm);
        _logPage = new LogPage(_logVm);
        _settingsPage = new SettingsPage(_settingsVm);
        _systemPage = new SystemPage(_systemVm);
        PageHost.Content = _systemPage;

        _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        _mainVm.ApplySettings(App.SettingsSvc.Settings);
        _mainVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText) || e.PropertyName == null)
                Dispatcher.BeginInvoke(() => StatusLeft.Text = _mainVm.StatusText);
        };

        BuildSidebarSystemInfo();
        StartPulse();
        MemCanvas.SizeChanged += (s, e) => DrawMemGraph();
        SampleMemory(); // 立即出首个读数

        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (s, e) =>
        {
            UpdateStatusBar();
            SampleMemory();
        };
        _statusTimer = statusTimer;
        statusTimer.Start();
        UpdateStatusBar();

        // 资源优化：窗口隐藏（托盘）时停止全部 UI 采样，显示时恢复
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible) _statusTimer.Start();
            else _statusTimer.Stop();
            UpdateSystemVmActivation();
        };
        Closed += (s, e) => _systemVm.Stop();

        // 关闭 = 最小化到托盘（可通过设置更改）；退出请使用托盘菜单
        Closing += (s, e) =>
        {
            if (!App.IsExiting && App.SettingsSvc.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
        };
        // 窗口真正关闭时置空 App 引用，托盘再打开时重建（否则 Show 已关闭窗口会抛异常）
        Closed += (s, e) => App.NotifyWindowClosed();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Win11 深色标题栏
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间 IsChecked="True" 会提前触发本事件，此时 PageHost 尚未创建
        if (PageHost == null) return;

        var tag = (string)((RadioButton)sender).Tag;
        UserControl page = tag switch
        {
            "rules" => _rulesPage,
            "log" => _logPage,
            "settings" => _settingsPage,
            "system" => _systemPage,
            _ => _processPage
        };
        if (ReferenceEquals(PageHost.Content, page)) return;
        PageHost.Content = page;

        // 仪表盘仅在激活时采样
        UpdateSystemVmActivation();

        // 页面切换淡入
        if (page is UIElement el)
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            el.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>仪表盘采样门控：窗口可见且当前页为系统信息时运行，否则停止（托盘零开销）</summary>
    private void UpdateSystemVmActivation()
    {
        if (IsVisible && ReferenceEquals(PageHost?.Content, _systemPage))
            _systemVm.Start();
        else
            _systemVm.Stop();
    }

    /// <summary>侧边栏系统信息：CPU 型号 + P/E 核格子图</summary>
    private void BuildSidebarSystemInfo()
    {
        try
        {
            // 注意：Registry.GetValue 必须带根键前缀，否则抛 ArgumentException（型号显示为"未知处理器"）
            var name = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", "") as string;
            SysCpuName.Text = string.IsNullOrWhiteSpace(name) ? "未知处理器" : name.Trim();
        }
        catch { SysCpuName.Text = "未知处理器"; }

        CoreMap.Children.Clear();
        foreach (var core in CpuTopology.Cores)
        {
            foreach (var idx in core.LogicalIndices)
            {
                var cell = new Border
                {
                    Width = 13,
                    Height = 13,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 3, 3),
                    Background = core.IsECore
                        ? new SolidColorBrush(Color.FromArgb(0x33, 0x6C, 0xCB, 0x5F))
                        : new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0xA4, 0x41)),
                    BorderBrush = core.IsECore
                        ? new SolidColorBrush(Color.FromArgb(0xCC, 0x6C, 0xCB, 0x5F))
                        : new SolidColorBrush(Color.FromArgb(0xCC, 0xD9, 0xA4, 0x41)),
                    BorderThickness = new Thickness(1),
                    ToolTip = $"LP {idx} · {core.KindText}"
                };
                CoreMap.Children.Add(cell);
            }
        }

        SysCoreSummary.Text = CpuTopology.HasECore
            ? $"{CpuTopology.LogicalCount}T · {CpuTopology.PCoreCount}P+{CpuTopology.ECoreCount}E"
            : $"{CpuTopology.LogicalCount}T · {CpuTopology.PCoreCount} P-CORES";
    }

    /// <summary>监控呼吸灯（琥珀 2s 循环）</summary>
    private void StartPulse()
    {
        var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PulseDot.BeginAnimation(OpacityProperty, pulse);
    }

    // ---------- 内存清理 ----------
    private bool _cleaningMemory;

    /// <summary>侧边栏清理按钮：后台一键清理，图标旋转反馈，结果回显 MemDetail</summary>
    private async void CleanMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaningMemory || App.MemClean == null) return;
        _cleaningMemory = true;
        MemCleanBtn.IsEnabled = false;

        // 图标旋转动画（清理中）
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        { RepeatBehavior = RepeatBehavior.Forever };
        var rt = new RotateTransform();
        MemCleanIcon.RenderTransform = rt;
        rt.BeginAnimation(RotateTransform.AngleProperty, spin);
        MemDetail.Text = "正在清理内存…";

        try
        {
            var r = await Task.Run(() => App.MemClean.CleanNow("手动"));
            // 等图表采样到清理后的数值再展示结果
            await Task.Delay(600);
            MemDetail.Text = r.Success
                ? $"清理完成 释放 {r.FreedMb:F0} MB（{r.BeforePct}%→{r.AfterPct}%）"
                : $"清理失败：{r.Error}";
        }
        catch (Exception ex)
        {
            MemDetail.Text = $"清理失败：{ex.Message}";
        }
        finally
        {
            rt.BeginAnimation(RotateTransform.AngleProperty, null);
            MemCleanIcon.RenderTransform = null;
            MemCleanBtn.IsEnabled = true;
            _cleaningMemory = false;
        }
    }

    /// <summary>采样物理内存占用并刷新迷你图表</summary>
    private void SampleMemory()
    {
        try
        {
            var m = NativeMethods.GetMemoryStatus();
            double pct = Math.Clamp(m.MemoryLoad, 0, 100);
            _memSamples.Enqueue(pct);
            while (_memSamples.Count > MemMaxSamples) _memSamples.Dequeue();

            MemPct.Text = $"{pct:0}%";
            double usedGb = (m.TotalPhys - m.AvailPhys) / 1024.0 / 1024 / 1024;
            double totalGb = m.TotalPhys / 1024.0 / 1024 / 1024;
            if (!_cleaningMemory)
                MemDetail.Text = $"{usedGb:0.0} / {totalGb:0.0} GB";

            // 占用过高转警示色
            var pctBrush = pct >= 90 ? FindResource("DangerBrush") as Brush
                : pct >= 75 ? FindResource("WarnBrush") as Brush
                : FindResource("AccentBrush") as Brush;
            MemPct.Foreground = pctBrush;
            MemLine.Stroke = pctBrush;
            byte a = pct >= 90 ? (byte)0x40 : (byte)0x26;
            byte r = pct >= 90 ? (byte)0xFF : (byte)0xD9;
            byte g = pct >= 90 ? (byte)0x6B : (byte)0xA4;
            byte b = pct >= 90 ? (byte)0x6B : (byte)0x41;
            MemFill.Fill = new SolidColorBrush(Color.FromArgb(a, r, g, b));

            DrawMemGraph();
        }
        catch { /* 内存查询失败保持上一帧 */ }
    }

    /// <summary>把采样序列绘制为琥珀迷你面积图</summary>
    private void DrawMemGraph()
    {
        double w = MemCanvas.ActualWidth, h = MemCanvas.ActualHeight;
        if (w < 10 || h < 10 || _memSamples.Count == 0) return;

        var pts = new List<Point>();
        int n = _memSamples.Count;
        int i = 0;
        foreach (var v in _memSamples)
        {
            double x = n == 1 ? w : (double)i / (n - 1) * w;
            double y = h - 1 - v / 100.0 * (h - 2); // 上下各留 1px
            pts.Add(new Point(x, y));
            i++;
        }

        MemLine.Points = new PointCollection(pts);

        // 填充：折线 + 底部闭合
        var fig = new PathFigure { StartPoint = new Point(pts[0].X, h) };
        foreach (var p in pts)
            fig.Segments.Add(new LineSegment(p, true));
        fig.Segments.Add(new LineSegment(new Point(pts[^1].X, h), true));
        MemFill.Data = new PathGeometry { Figures = { fig } };
    }

    private void UpdateStatusBar()
    {
        StatusProc.Text = $"PROC {_mainVm.ProcessCount:000}";
        var s = App.SettingsSvc.Settings;
        StatusAuto.Text = $"自动应用 {(s.AutoApply ? "开" : "关")} · {s.PollIntervalMs / 1000.0:0.#}s";
        PulseText.Text = s.AutoApply ? "监控运行中" : "监控已暂停";
        PulseDot.Fill = s.AutoApply
            ? FindResource("AccentBrush") as Brush
            : FindResource("TextFaint") as Brush;

        StatusPriv.Text = _isAdmin ? "ADMIN" : "USER";
        StatusPriv.Foreground = _isAdmin
            ? FindResource("SuccessBrush") as Brush
            : FindResource("WarnBrush") as Brush;
        PrivBadge.Background = _isAdmin
            ? new SolidColorBrush(Color.FromArgb(0x33, 0x6C, 0xCB, 0x5F))
            : new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0xA4, 0x41));
    }
}
