using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ProcOpt.ViewModels;

namespace ProcOpt.Views.Pages;

/// <summary>系统信息仪表盘：4 张 60 秒趋势图（CPU/内存/GPU/网络）。
/// 数据轮询由 MainWindow 门控（仅本页可见且窗口显示时运行），页面订阅 VM.Ticked 重绘。
/// 注意：订阅与页面同生命周期，不随 Unloaded 取消（否则托盘隐藏后图表不再更新）。</summary>
public partial class SystemPage : UserControl
{
    private readonly SystemViewModel _vm;
    private readonly List<Rectangle> _coreBars = new();   // 每核迷你条（按逻辑核数重建）
    private bool _perfWide = true;                        // 性能四卡当前是否为宽屏一行布局（初始 true 保证首次必定布点）

    public SystemPage(SystemViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        if (!_vm.GpuAvailable)
            GpuPlaceholder.Visibility = Visibility.Visible;   // 非 NVIDIA 显卡：图表区空状态提示

        Loaded += (s, e) => Repaint();
        SizeChanged += (s, e) => UpdatePerfLayout();
        UpdatePerfLayout();   // 构造时先按 2×2 落位，实际尺寸确定后由 SizeChanged 校正
        CpuCanvas.SizeChanged += (s, e) => Repaint();
        MemCanvas.SizeChanged += (s, e) => Repaint();
        GpuCanvas.SizeChanged += (s, e) => Repaint();
        NetCanvas.SizeChanged += (s, e) => Repaint();
        CoreCanvas.SizeChanged += (s, e) => PaintCores();

        vm.Ticked += OnTicked;
    }

    private void OnTicked() => Dispatcher.BeginInvoke(Repaint);

    /// <summary>性能四卡自适应布局：可用宽度 ≥ 1280 时一行四卡，否则 2×2 两行（窗口拖动实时切换）</summary>
    private void UpdatePerfLayout()
    {
        bool wide = ActualWidth >= 1280;
        if (wide == _perfWide) return;   // 布局未变化则跳过，避免拖动窗口时反复重建
        _perfWide = wide;

        PerfGrid.ColumnDefinitions.Clear();
        PerfGrid.RowDefinitions.Clear();
        void AddStar() => PerfGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void AddGap() => PerfGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        void Place(UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
        }

        if (wide)
        {
            PerfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddStar(); AddGap(); AddStar(); AddGap(); AddStar(); AddGap(); AddStar();
            Place(CpuCard, 0, 0); Place(MemCard, 0, 2); Place(GpuCard, 0, 4); Place(NetCard, 0, 6);
        }
        else
        {
            PerfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PerfGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddStar(); AddGap(); AddStar();
            Place(CpuCard, 0, 0); Place(MemCard, 0, 2); Place(GpuCard, 1, 0); Place(NetCard, 1, 2);
        }
    }

    /// <summary>切换图表窗口 1 分钟 / 5 分钟</summary>
    private void ChartWindow_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleChartWindow();
        Repaint();
    }

    /// <summary>复制系统报告，按钮文字短暂反馈结果</summary>
    private async void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var ok = _vm.CopyReport();
        CopyReportBtn.Content = ok ? "已复制 ✓" : "复制失败";
        await Task.Delay(1500);
        CopyReportBtn.Content = "复制报告";
    }

    private void Repaint()
    {
        PaintScaled(CpuCanvas, CpuFill, CpuLine, _vm.CpuHistory());
        PaintScaled(MemCanvas, MemFill, MemLine, _vm.MemHistory());
        PaintScaled(GpuCanvas, GpuFill, GpuLine, _vm.GpuHistory());
        PaintNet();
        PaintCores();
    }

    /// <summary>每逻辑核占用迷你条（任务管理器风格竖条）</summary>
    private void PaintCores()
    {
        var v = _vm.PerCoreUsage;
        double w = CoreCanvas.ActualWidth, h = CoreCanvas.ActualHeight;
        if (v.Length == 0 || w <= 1) return;

        // 核心数变化时重建竖条
        if (_coreBars.Count != v.Length)
        {
            CoreCanvas.Children.Clear();
            _coreBars.Clear();
            for (int i = 0; i < v.Length; i++)
            {
                var r = new Rectangle { RadiusX = 1.5, RadiusY = 1.5 };
                r.Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x8A, 0x96, 0xF5));
                r.Fill.Freeze();
                _coreBars.Add(r);
                CoreCanvas.Children.Add(r);
            }
        }

        const double gap = 2.5;
        double bw = Math.Max(2, (w - gap * (v.Length - 1)) / v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            var bar = _coreBars[i];
            bar.Width = bw;
            bar.Height = Math.Max(2, v[i] / 100.0 * (h - 2));
            Canvas.SetLeft(bar, i * (bw + gap));
            Canvas.SetTop(bar, h - bar.Height);
        }
    }

    /// <summary>0-100 百分比趋势面积图</summary>
    private static void PaintScaled(Canvas canvas, Path fill, Polyline line, double[] v)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 1 || h <= 1 || v.Length < 2)
        {
            line.Points.Clear();
            fill.Data = null;
            return;
        }

        double dx = w / (v.Length - 1);   // 窗口内均分（样本数随窗口切换变化）
        var pts = new PointCollection(v.Length);
        for (int i = 0; i < v.Length; i++)
            pts.Add(new Point(i * dx, h - 2 - Math.Clamp(v[i], 0, 100) / 100.0 * (h - 6)));

        line.Points = pts;
        fill.Data = Area(pts, h);
    }

    /// <summary>网络图：下行面积+曲线，上行叠加曲线，按峰值自动缩放</summary>
    private void PaintNet()
    {
        double w = NetCanvas.ActualWidth, h = NetCanvas.ActualHeight;
        var d = _vm.NetDownHistory();
        var u = _vm.NetUpHistory();
        if (w <= 1 || h <= 1 || d.Length < 2 || u.Length < 2)
        {
            NetLineDown.Points.Clear();
            NetLineUp.Points.Clear();
            NetFillDown.Data = null;
            return;
        }

        double max = 50 * 1024; // 坐标下限 50 KB/s
        foreach (var x in d) if (x > max) max = x;
        foreach (var x in u) if (x > max) max = x;
        max *= 1.1;

        double dx = w / (d.Length - 1);   // 窗口内均分
        var pd = new PointCollection(d.Length);
        for (int i = 0; i < d.Length; i++)
            pd.Add(new Point(i * dx, h - 2 - Math.Clamp(d[i], 0, max) / max * (h - 6)));
        NetLineDown.Points = pd;
        NetFillDown.Data = Area(pd, h);

        var pu = new PointCollection(u.Length);
        for (int i = 0; i < u.Length; i++)
            pu.Add(new Point(i * dx, h - 2 - Math.Clamp(u[i], 0, max) / max * (h - 6)));
        NetLineUp.Points = pu;
    }

    /// <summary>折线下方到画布底部的封闭填充区域</summary>
    private static StreamGeometry Area(PointCollection pts, double h)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
            ctx.LineTo(new Point(pts[^1].X, h), true, false);
            ctx.LineTo(new Point(pts[0].X, h), true, false);
        }
        g.Freeze();
        return g;
    }
}
