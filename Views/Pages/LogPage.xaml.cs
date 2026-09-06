using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ProcOpt.ViewModels;

namespace ProcOpt.Views.Pages;

public partial class LogPage : UserControl
{
    public LogPage(LogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        UpdateEmptyState();
        vm.Entries.CollectionChanged += (s, e) => UpdateEmptyState();
        Loaded += (s, e) => UpdateEmptyState();
    }

    private void UpdateEmptyState()
        => Dispatcher.Invoke(() =>
            EmptyState.Visibility = (DataContext as LogViewModel)?.Entries.Count == 0
                ? Visibility.Visible : Visibility.Collapsed);
}
