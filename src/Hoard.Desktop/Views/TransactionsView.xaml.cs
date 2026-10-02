using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class TransactionsView : UserControl
{
    public TransactionsView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await Vm.ApplyFilterCommand.ExecuteAsync(null);
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var vm = new ImportViewModel(Vm.ImportService, Vm.AccountRepository, Vm.CategoryRepository);
        var dlg = new ImportWindow { Owner = Window.GetWindow(this), DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed)
        {
            await Vm.ReloadTransactionCategoriesAsync();
            await Vm.LoadTransactionsAsync();
            await Vm.RefreshAsync();
            Vm.StatusMessage = vm.Status;
        }
    }

    private async void OnCategoriesRulesClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this)!;
        var vm = new CategoriesRulesViewModel(Vm.CategoryRepository, Vm.Categorization, Vm.AccountRepository);
        var dlg = new CategoriesRulesWindow { Owner = owner, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed)
        {
            await Vm.ReloadTransactionCategoriesAsync();
            await Vm.LoadTransactionsAsync();
            Vm.MarkDataChanged();
            if (!string.IsNullOrEmpty(vm.Status)) Vm.StatusMessage = vm.Status;
        }
    }
}
