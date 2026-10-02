using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class DemoDatabaseWindow : Wpf.Ui.Controls.FluentWindow
{
    public DemoDatabaseWindow()
    {
        InitializeComponent();
    }

    private void OnAdminPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DemoDatabaseViewModel vm) vm.AdminPassword = AdminPasswordBox.Password;
    }
}
