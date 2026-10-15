using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Core.Providers.Plaid;
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

    private async void OnAddPlaidClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AddPlaidWindow { Owner = Owner, DataContext = new AddPlaidViewModel(Vm.InstitutionService) };
        dlg.ShowDialog();
        await Vm.RefreshAsync();
    }

    /// <summary>Plaid update mode: the user signs in to the bank again in the browser; the Item and its accounts stay as they are.</summary>
    private async void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedInstitution is not { IsPlaid: true } inst) return;
        const string title = "Hoard - Reconnect Plaid login";
        try
        {
            var (session, keys) = await Vm.InstitutionService.StartPlaidReconnectAsync(inst.Id);
            Process.Start(new ProcessStartInfo(session.HostedLinkUrl) { UseShellExecute = true });
            System.Windows.MessageBox.Show(Owner, $"Plaid Link is open in your browser. Sign in to {inst.Name} there, then click OK.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var status = await Vm.InstitutionService.WaitForPlaidLinkAsync(keys, session.LinkToken, updateMode: true, cts.Token);
            if (status.State == PlaidLinkState.Succeeded)
            {
                await Vm.InstitutionService.CompletePlaidReconnectAsync(inst.Id);
                await Vm.SyncSelectedCommand.ExecuteAsync(null);
            }
            else
            {
                System.Windows.MessageBox.Show(Owner, status.Message ?? "Plaid Link was closed before signing in.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            System.Windows.MessageBox.Show(Owner, "Plaid has not reported the sign-in as finished yet. Try Reconnect again once you have completed it in the browser.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Owner, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            $"Remove \"{inst.Name}\"?\n\nThis deletes its stored credential, all of its accounts, and every downloaded transaction." +
            (inst.IsPlaid ? "\n\nAccounts it took over from another connection (e.g. SimpleFIN) are handed back to it with their history instead of being deleted, and the bank login is removed at Plaid." : ""),
            "Hoard - Remove institution", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.RemoveSelectedCommand.ExecuteAsync(null);
    }
}
