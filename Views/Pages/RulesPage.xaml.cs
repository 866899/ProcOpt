using System.Windows;
using System.Windows.Controls;
using ProcOpt.Models;
using ProcOpt.ViewModels;

namespace ProcOpt.Views.Pages;

public partial class RulesPage : UserControl
{
    private readonly RulesViewModel _vm;

    public RulesPage(RulesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        UpdateEmptyState();
        App.Rules.RulesChanged += UpdateEmptyState;
        App.Rules.Rules.CollectionChanged += (s, e) => UpdateEmptyState();
        Loaded += (s, e) => UpdateEmptyState();
    }

    private void UpdateEmptyState()
        => Dispatcher.Invoke(() =>
            EmptyState.Visibility = App.Rules.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed);

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcessRule rule)
            _vm.Toggle(rule);
    }
}
