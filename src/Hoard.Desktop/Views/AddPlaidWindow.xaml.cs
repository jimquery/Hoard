using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class AddPlaidWindow : Wpf.Ui.Controls.FluentWindow
{
    public AddPlaidWindow()
    {
        InitializeComponent();
    }

    private AddPlaidViewModel Vm => (AddPlaidViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private void OnSecretChanged(object sender, RoutedEventArgs e) => Vm.Secret = SecretBox.Password;

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Also runs for the title-bar close button, so an Item linked but not saved is always discarded.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SecretBox.Clear();
        await Vm.CloseAsync();
    }
}
