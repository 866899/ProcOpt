using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ProcOpt.Infrastructure;
using ProcOpt.Models;
using ProcOpt.Services;
using ProcOpt.Views;

namespace ProcOpt.ViewModels;

public class RulesViewModel : ObservableObject
{
    public ObservableCollection<ProcessRule> Rules => App.Rules.Rules;

    public ICommand AddCommand => new RelayCommand(() => EditRule(null));

    /// <summary>导出全部规则为 JSON 文件（与 rules.json 同格式，可跨机器分享）</summary>
    public ICommand ExportCommand => new RelayCommand(() =>
    {
        if (Rules.Count == 0)
        {
            MessageBox.Show("当前没有规则可导出。", "导出规则",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出规则",
            Filter = "ProcOpt 规则文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"ProcOpt规则_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(Rules.ToList(), opts));
            MessageBox.Show($"已导出 {Rules.Count} 条规则到：\n{dlg.FileName}", "导出规则",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "导出规则",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    /// <summary>从 JSON 文件导入规则：跳过同名与无效项，新增项重新分配 Id</summary>
    public ICommand ImportCommand => new RelayCommand(() =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入规则",
            Filter = "ProcOpt 规则文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var imported = JsonSerializer.Deserialize<List<ProcessRule>>(File.ReadAllText(dlg.FileName));
            if (imported == null || imported.Count == 0)
            {
                MessageBox.Show("文件中没有可导入的规则。", "导入规则",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int added = 0, skipped = 0, invalid = 0;
            foreach (var r in imported)
            {
                if (r == null || !r.IsValid()) { invalid++; continue; }
                if (Rules.Any(x => ProcessRule.NormalizeName(x.ProcessName) == ProcessRule.NormalizeName(r.ProcessName)))
                { skipped++; continue; }
                r.Id = Guid.NewGuid(); // 重新分配 Id 避免与现有规则冲突
                Rules.Add(r);
                added++;
            }
            App.Rules.Save();

            var summary = $"导入完成：新增 {added} 条";
            if (skipped > 0) summary += $"，跳过同名 {skipped} 条";
            if (invalid > 0) summary += $"，无效 {invalid} 条";
            MessageBox.Show(summary + "。", "导入规则",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：文件格式不正确或无法读取。\n{ex.Message}", "导入规则",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    public ICommand EditCommand => new RelayCommand(param =>
    {
        if (param is ProcessRule r) EditRule(r);
    });

    public ICommand DeleteCommand => new RelayCommand(param =>
    {
        if (param is not ProcessRule r) return;
        if (MessageBox.Show($"确定删除规则「{r.ProcessName}」吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Rules.Rules.Remove(r);
        App.Rules.Save();
    });

    /// <summary>切换启用状态（由页面复选框触发）</summary>
    public void Toggle(ProcessRule rule)
    {
        App.Rules.Save();
    }

    public ICommand ApplyNowCommand => new RelayCommand(param =>
    {
        if (param is not ProcessRule r || !r.Enabled) return;
        int n = App.Engine.ApplyToRunning(r);
        MessageBox.Show(n == 0
            ? "未找到正在运行的同名进程。"
            : $"已应用到 {n} 个正在运行的进程。", "立即应用",
            MessageBoxButton.OK, MessageBoxImage.Information);
    });

    private void EditRule(ProcessRule existing)
    {
        var win = new RuleEditWindow(existing)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        if (win.ShowDialog() != true) return;

        var result = win.Result;
        if (existing == null)
            App.Rules.Rules.Add(result);
        else
            App.Rules.Rules[App.Rules.Rules.IndexOf(existing)] = result;
        App.Rules.Save();

        if (win.ApplyToRunning)
        {
            int n = App.Engine.ApplyToRunning(result);
            MessageBox.Show(n == 0
                ? "规则已保存。当前未发现正在运行的同名进程。"
                : $"规则已保存，并应用到 {n} 个正在运行的进程。", "ProcOpt",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
