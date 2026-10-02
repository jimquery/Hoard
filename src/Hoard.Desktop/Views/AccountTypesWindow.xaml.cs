using System.Windows;
using System.Windows.Controls;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class AccountTypesWindow : Wpf.Ui.Controls.FluentWindow
{
    public AccountTypesWindow()
    {
        InitializeComponent();
    }

    private AccountTypesViewModel Vm => (AccountTypesViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } button) return;
        // Walk up to the row's data item.
        DependencyObject? current = button;
        while (current is not null && current is not DataGridRow) current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        if (current is DataGridRow { Item: AccountTypeItem item })
        {
            item.Color = hex;
            Vm.MarkChanged();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } t) return;
        var detail = t.AccountCount > 0 ? $"\n\n{t.AccountCount} account(s) currently use it and will become untyped." : "";
        var answer = System.Windows.MessageBox.Show(this, $"Delete the account type \"{t.Name}\"?{detail}", "Hoard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.DeleteCommand.ExecuteAsync(null);
    }
}
