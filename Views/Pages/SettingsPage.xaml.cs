using System.Windows.Controls;
using ProcOpt.ViewModels;

namespace ProcOpt.Views.Pages;

public partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (s, e) => _vm.RefreshInfoCommand.Execute(null);
    }
}
