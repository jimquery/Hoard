using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class InstitutionsView : UserControl
{
    public InstitutionsView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;
    private Window Owner => Window.GetWindow(this)!;

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AddInstitutionWindow { Owner = Owner, DataContext = new AddInstitutionViewModel(Vm.InstitutionService) };
        if (dlg.ShowDialog() == true) await Vm.RefreshAsync();
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => await RenameSelectedAsync();

    private async Task RenameSelectedAsync()
    {
        if (Vm.SelectedInstitution is not { } inst) return;
        var name = InputDialog.Show(Owner, "Rename institution", "New name", inst.Name);
        if (name is not null) await Vm.RenameInstitutionAsync(inst, name);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedInstitution is not { } inst) return;
        var answer = System.Windows.MessageBox.Show(Owner,
            $"Remove \"{inst.Name}\"?\n\nThis deletes its stored credential, all of its accounts, and every downloaded transaction.",
            "Hoard - Remove institution", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.RemoveSelectedCommand.ExecuteAsync(null);
    }
}
