using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class AddInstitutionWindow : Wpf.Ui.Controls.FluentWindow
{
    public AddInstitutionWindow()
    {
        InitializeComponent();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = DataContext is AddInstitutionViewModel { Succeeded: true };
        Close();
    }
}
