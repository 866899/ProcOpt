using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ProcOpt.Services;

namespace ProcOpt.Views;

public partial class AffinityWindow : Window
{
    private readonly Dictionary<int, ToggleButton> _chips = new();

    public long SelectedMask { get; private set; }

    public AffinityWindow(long currentMask)
    {
        InitializeComponent();
        BuildChips(currentMask);
        if (CpuTopology.HasECore)
        {
            BtnEOnly.Visibility = Visibility.Visible;
            EGroupPanel.Visibility = Visibility.Visible;
        }
        if (!CpuTopology.HasECore) BtnPOnly.Visibility = Visibility.Collapsed;
        UpdateMaskText();
    }

    private void BuildChips(long mask)
    {
        foreach (var core in CpuTopology.Cores)
        {
            foreach (var idx in core.LogicalIndices)
            {
                var tip = core.LogicalIndices.Count > 1
                    ? $"逻辑处理器 {idx} · {core.KindText}（同核线程：{string.Join("、", core.LogicalIndices)}）"
                    : $"逻辑处理器 {idx} · {core.KindText}";
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("CoreChip"),
                    Content = idx.ToString(),
                    IsChecked = (mask >> idx & 1) != 0,
                    BorderBrush = core.IsECore ? FindResource("ECoreBrush") as Brush : FindResource("PCoreBrush") as Brush,
                    ToolTip = tip
                };
                chip.Checked += (s, e) => UpdateMaskText();
                chip.Unchecked += (s, e) => UpdateMaskText();
                _chips[idx] = chip;
                (core.IsECore ? EPanel : PPanel).Children.Add(chip);
            }
        }
    }

    private void SetChips(long mask)
    {
        foreach (var (idx, chip) in _chips)
            chip.IsChecked = (mask >> idx & 1) != 0;
        UpdateMaskText();
    }

    private void All_Click(object sender, RoutedEventArgs e) => SetChips(CpuTopology.AllMask);
    private void POnly_Click(object sender, RoutedEventArgs e) => SetChips(CpuTopology.OnlyPMask);
    private void EOnly_Click(object sender, RoutedEventArgs e) => SetChips(CpuTopology.OnlyEMask);

    private long CurrentMask()
    {
        long m = 0;
        foreach (var (idx, chip) in _chips)
            if (chip.IsChecked == true) m |= 1L << idx;
        return m;
    }

    private void UpdateMaskText()
    {
        var m = CurrentMask();
        MaskText.Text = $"当前选择：{Models.ProcessItemView.MaskToText(m)}（{System.Numerics.BitOperations.PopCount((ulong)m)} / {CpuTopology.LogicalCount} 线程）";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var m = CurrentMask();
        if (m == 0)
        {
            MessageBox.Show("请至少选择一个逻辑处理器。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedMask = m;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
