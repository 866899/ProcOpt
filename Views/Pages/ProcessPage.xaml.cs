using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProcOpt.Models;
using ProcOpt.Services;
using ProcOpt.ViewModels;

namespace ProcOpt.Views.Pages;

public partial class ProcessPage : UserControl
{
    private readonly MainViewModel _vm;

    public ProcessPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SelectedProcesses = Grid.SelectedItems.Cast<ProcessItemView>().ToList();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.RefreshCommand.Execute(null);

    private void ApplyPri_Click(object sender, RoutedEventArgs e)
    {
        if (PriCombo.SelectedItem is not ComboBoxItem item || item.Tag == null) return;
        _vm.ApplyPriorityCommand.Execute((string)item.Tag);
    }

    private void PriMenu_Click(object sender, RoutedEventArgs e)
        => _vm.ApplyPriorityCommand.Execute((string)((MenuItem)sender).Tag);

    private void EcoOn_Click(object sender, RoutedEventArgs e) => _vm.ApplyEcoCommand.Execute("on");
    private void EcoOff_Click(object sender, RoutedEventArgs e) => _vm.ApplyEcoCommand.Execute("off");
    private void EcoMenu_Click(object sender, RoutedEventArgs e)
        => _vm.ApplyEcoCommand.Execute((string)((MenuItem)sender).Tag);

    private void CleanMem_Click(object sender, RoutedEventArgs e) => _vm.CleanMemoryCommand.Execute(null);

    private void Affinity_Click(object sender, RoutedEventArgs e)
    {
        var targets = _vm.SelectedProcesses.Where(p => p != null).ToList();
        if (targets.Count == 0)
        {
            _vm.StatusText = "请先选择进程";
            return;
        }
        var current = targets[0].AffinityMask ?? CpuTopology.AllMask;
        var win = new AffinityWindow(current) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;

        int ok = 0, fail = 0;
        foreach (var t in targets)
        {
            try { ProcessService.SetAffinity(t.Pid, win.SelectedMask); ok++; }
            catch { fail++; }
        }
        _vm.StatusText = $"相关性已应用到 {ok} 个进程" + (fail > 0 ? $"（{fail} 个失败）" : "");
        _ = _vm.RefreshAsync();
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        var first = _vm.SelectedProcesses.FirstOrDefault(p => p != null);
        if (first == null)
        {
            _vm.StatusText = "请先选择进程";
            return;
        }
        var seed = new ProcessRule
        {
            ProcessName = first.Name,
            SetPriority = first.Priority != null,
            Priority = first.Priority ?? ProcessPriorityClass.Normal,
            SetEco = first.EcoOn != null,
            EcoEnabled = first.EcoOn == true,
            SetAffinity = false,
            AffinityMask = first.AffinityMask ?? CpuTopology.AllMask
        };
        var win = new RuleEditWindow(null, seed) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;
        App.Rules.Rules.Add(win.Result);
        App.Rules.Save();
        _vm.StatusText = $"已创建规则：{win.Result.ProcessName}";
    }

    private void SelectSame_Click(object sender, RoutedEventArgs e)
    {
        var names = _vm.SelectedProcesses.Where(p => p != null).Select(p => p.Name).ToHashSet();
        if (names.Count == 0) return;
        foreach (var item in Grid.Items.Cast<ProcessItemView>().Where(p => names.Contains(p.Name)))
        {
            if (Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                row.IsSelected = true;
        }
    }
}
