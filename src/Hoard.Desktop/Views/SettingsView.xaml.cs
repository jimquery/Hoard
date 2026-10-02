using System.Windows;
using System.Windows.Controls;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"Hoard {typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "dev"}";
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private void OnDbPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.DbPassword = DbPasswordBox.Password;
    }

    private void OnCreateDemoClick(object sender, RoutedEventArgs e)
    {
        var vm = new DemoDatabaseViewModel(Vm.Settings, Vm.Protector, Vm.CurrentDbPassword());
        var dlg = new DemoDatabaseWindow { Owner = Window.GetWindow(this), DataContext = vm };
        dlg.ShowDialog();
    }
}
