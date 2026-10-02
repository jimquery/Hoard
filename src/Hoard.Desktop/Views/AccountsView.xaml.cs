using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;
    private Window Owner => Window.GetWindow(this)!;

    private async void OnRenameInstitutionClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedInstitution is not { } inst) return;
        var name = InputDialog.Show(Owner, "Rename institution", "New name", inst.Name);
        if (name is not null) await Vm.RenameInstitutionAsync(inst, name);
    }

    private async void OnRemoveInstitutionClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedInstitution is not { } inst) return;
        var answer = System.Windows.MessageBox.Show(Owner,
            $"Remove \"{inst.Name}\"?\n\nThis deletes its stored credential, all of its accounts, and every downloaded transaction.",
            "Hoard - Remove institution", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.RemoveSelectedCommand.ExecuteAsync(null);
    }

    private async void OnRenameAccountClick(object sender, RoutedEventArgs e) => await RenameSelectedAccountAsync();

    private async void OnAccountDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ComboBox>(source) is not null) return; // editing the type
        await RenameSelectedAccountAsync();
    }

    private async Task RenameSelectedAccountAsync()
    {
        if (Vm.SelectedAccount is not { } acct) return;
        var name = InputDialog.Show(Owner, "Rename account", $"Display name (bank calls it \"{acct.BankName}\"; leave as the bank name to clear)", acct.Name);
        if (name is not null) await Vm.RenameAccountAsync(acct, name);
    }

    private async void OnResetAccountNameClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedAccount is { } acct) await Vm.RenameAccountAsync(acct, null);
    }

    private async void OnAccountTypesClick(object sender, RoutedEventArgs e)
    {
        var vm = new AccountTypesViewModel(Vm.AccountRepository);
        var dlg = new AccountTypesWindow { Owner = Owner, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed) await Vm.RefreshAsync();
    }

    private async void OnHouseholdClick(object sender, RoutedEventArgs e)
    {
        var vm = new HouseholdViewModel(Vm.HouseholdRepository);
        var dlg = new HouseholdWindow { Owner = Owner, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed) await Vm.RefreshAsync();
    }

    private async void OnMergeDuplicatesClick(object sender, RoutedEventArgs e)
    {
        var vm = new MergeAccountsViewModel(Vm.MergeService, Vm.AccountRepository);
        var dlg = new MergeAccountsWindow { Owner = Owner, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed)
        {
            await Vm.RefreshAsync();
            if (!string.IsNullOrEmpty(vm.Status)) Vm.StatusMessage = vm.Status;
        }
    }

    private async void OnRealEstateClick(object sender, RoutedEventArgs e)
    {
        var vm = new PropertiesViewModel(Vm.PropertyService);
        var dlg = new PropertiesWindow { Owner = Owner, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed) await Vm.RefreshAsync();
    }

    private static T? FindParent<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
