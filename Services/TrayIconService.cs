using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ProcOpt.Services;

namespace ProcOpt.Services;

public class TrayIconService : IDisposable
{
    private NotifyIcon _notify;
    private Bitmap _iconBitmap;
    private Icon _icon;
    private ToolStripMenuItem _autoApplyItem;

    public void Init(Action openWindow, Func<bool> isAutoApplyOn, Action<bool> setAutoApply, Action exit,
        Action cleanMemory = null)
    {
        _iconBitmap = DrawIcon();
        _icon = Icon.FromHandle(_iconBitmap.GetHicon());

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(43, 43, 43),
            ForeColor = Color.FromArgb(240, 240, 240),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _autoApplyItem = new ToolStripMenuItem("自动应用规则") { CheckOnClick = true };
        _autoApplyItem.CheckedChanged += (s, e) =>
        {
            if (_autoApplyItem.Checked != isAutoApplyOn()) setAutoApply(_autoApplyItem.Checked);
        };
        var openItem = new ToolStripMenuItem("打开主窗口", null, (s, e) => openWindow());
        var cleanItem = new ToolStripMenuItem("清理内存", null, (s, e) => cleanMemory?.Invoke());
        var exitItem = new ToolStripMenuItem("退出", null, (s, e) => exit());

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoApplyItem);
        if (cleanMemory != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(cleanItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "ProcOpt - 进程调优",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += (s, e) => openWindow();
        _autoApplyItem.Checked = isAutoApplyOn();
    }

    /// <summary>外部状态变化时同步托盘菜单勾选</summary>
    public void SyncAutoApply(bool on) { if (_autoApplyItem != null) _autoApplyItem.Checked = on; }

    public void ShowBalloon(string title, string message)
    {
        _notify?.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
    }

    private static Bitmap DrawIcon()
    {
        const int size = 32;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new GraphicsPath();
        int r = 8;
        var rect = new Rectangle(1, 1, size - 2, size - 2);
        bg.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        bg.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        bg.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        bg.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        bg.CloseFigure();
        using (var brush = new LinearGradientBrush(rect, Color.FromArgb(96, 125, 255), Color.FromArgb(60, 78, 170), 45f))
            g.FillPath(brush, bg);
        // 闪电图形（能效/性能调优意象）
        var bolt = new[]
        {
            new PointF(19, 5), new PointF(10, 18), new PointF(15.5f, 18),
            new PointF(13, 27), new PointF(22.5f, 14), new PointF(16.8f, 14), new PointF(19, 5)
        };
        g.FillPolygon(Brushes.White, bolt);
        return bmp;
    }

    public void Dispose()
    {
        if (_notify != null)
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        _icon?.Dispose();
        _iconBitmap?.Dispose();
    }
}
