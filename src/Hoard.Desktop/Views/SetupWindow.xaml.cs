using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class SetupWindow : Wpf.Ui.Controls.FluentWindow
{
    public SetupWindow()
    {
        InitializeComponent();
    }

    private async void OnProvisionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupViewModel vm) return;
        vm.AdminPassword = AdminPasswordBox.Password;
        await vm.ProvisionCommand.ExecuteAsync(null);
        AdminPasswordBox.Clear();
        vm.AdminPassword = "";
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
