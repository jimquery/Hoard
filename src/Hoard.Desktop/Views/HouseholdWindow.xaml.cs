using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class HouseholdWindow : Wpf.Ui.Controls.FluentWindow
{
    public HouseholdWindow()
    {
        InitializeComponent();
    }

    private HouseholdViewModel Vm => (HouseholdViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } m) return;
        var detail = m.AccountCount > 0 ? $"\n\n{m.AccountCount} account(s) list {m.Name} as owner and will become unowned." : "";
        var answer = System.Windows.MessageBox.Show(this, $"Remove {m.Name} from the household?{detail}", "Hoard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.DeleteCommand.ExecuteAsync(null);
    }
}
