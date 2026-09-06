using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ProcOpt.Models;
using ProcOpt.Services;

namespace ProcOpt.Views;

public partial class RuleEditWindow : Window
{
    private readonly ProcessRule _working;
    private long _affMask;

    public ProcessRule Result { get; private set; }
    public bool ApplyToRunning => ApplyNowCheck.IsChecked == true;

    /// <summary>existing=null 表示新建；seed 提供预填充</summary>
    public RuleEditWindow(ProcessRule existing, ProcessRule seed = null)
    {
        InitializeComponent();
        _working = (existing ?? seed)?.Clone() ?? new ProcessRule();
        TitleText.Text = existing != null ? "编辑规则" : "新建规则";

        NameBox.Text = _working.ProcessName;
        PriCheck.IsChecked = _working.SetPriority;
        EcoCheck.IsChecked = _working.SetEco;
        EcoOnRadio.IsChecked = _working.EcoEnabled;
        AffCheck.IsChecked = _working.SetAffinity;
        _affMask = _working.AffinityMask == 0 ? CpuTopology.AllMask : _working.AffinityMask;
        ApplyNowCheck.IsChecked = existing == null && seed != null; // 从进程保存时默认立即应用
        SyncEnableState();
        SyncAffText();
        SelectPriority();
    }

    private void SelectPriority()
    {
        foreach (ComboBoxItem item in PriCombo.Items)
            if ((string)item.Tag == _working.Priority.ToString())
            {
                PriCombo.SelectedItem = item;
                return;
            }
        PriCombo.SelectedIndex = 3; // 正常
    }

    private void Check_Changed(object sender, RoutedEventArgs e) => SyncEnableState();

    private void SyncEnableState() => PriCombo.IsEnabled = PriCheck.IsChecked == true;

    private void SyncAffText()
        => AffText.Text = $"已选择：{ProcessItemView.MaskToText(_affMask)}";

    private void PickAffinity_Click(object sender, RoutedEventArgs e)
    {
        var win = new AffinityWindow(_affMask) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _affMask = win.SelectedMask;
            SyncAffText();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var rule = _working.Clone();
        rule.ProcessName = NameBox.Text.Trim();
        rule.SetPriority = PriCheck.IsChecked == true;
        if (PriCombo.SelectedItem is ComboBoxItem pri) rule.Priority = Enum.Parse<ProcessPriorityClass>((string)pri.Tag);
        rule.SetEco = EcoCheck.IsChecked == true;
        rule.EcoEnabled = EcoOnRadio.IsChecked == true;
        rule.SetAffinity = AffCheck.IsChecked == true;
        rule.AffinityMask = _affMask;

        if (!rule.IsValid())
        {
            MessageBox.Show("请填写进程名，并至少启用一项设置（相关性需选择处理器）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = rule;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
