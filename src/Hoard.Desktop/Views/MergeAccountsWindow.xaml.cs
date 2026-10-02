using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class MergeAccountsWindow : Wpf.Ui.Controls.FluentWindow
{
    public MergeAccountsWindow()
    {
        InitializeComponent();
    }

    private MergeAccountsViewModel Vm => (MergeAccountsViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.Confirm = question => System.Windows.MessageBox.Show(this, question, "Hoard - Merge accounts",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        await Vm.LoadAsync();
    }
}
