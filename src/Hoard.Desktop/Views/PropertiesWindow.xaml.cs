using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class PropertiesWindow : Wpf.Ui.Controls.FluentWindow
{
    public PropertiesWindow()
    {
        InitializeComponent();
    }

    private PropertiesViewModel Vm => (PropertiesViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await Vm.LoadAsync(); }
        catch (Exception ex) { Vm.Status = ex.Message; }
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var editor = new PropertyEditorViewModel(existing: null) { UseOnlineValue = Vm.HasApiKey };
        await EditAsync(editor);
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } item) return;
        await EditAsync(new PropertyEditorViewModel(item.Model));
    }

    private async Task EditAsync(PropertyEditorViewModel editor)
    {
        var dlg = new PropertyEditorWindow { Owner = this, DataContext = editor };
        if (dlg.ShowDialog() != true) return;
        Vm.Status = editor.IsNew ? $"Added {editor.Model.Nickname}." : $"Updated {editor.Model.Nickname}.";
        if (editor.UseOnlineValue && Vm.HasApiKey)
        {
            // Value a new (or newly online) property right away; properties that are not due are left alone.
            await Vm.RefreshDueCommand.ExecuteAsync(null);
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } item) return;
        var answer = System.Windows.MessageBox.Show(this,
            $"Remove {item.Nickname}?\n\nIts account and value history will be deleted from Hoard.",
            "Hoard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.RemoveCommand.ExecuteAsync(null);
    }

    private async void OnApiKeyClick(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog(
            "RentCast API key",
            "Paste your RentCast API key. Create a free account at app.rentcast.io, open the API dashboard and create a key. " +
            "The free Developer plan allows 50 requests per month; Hoard uses one request per property per refresh. " +
            "The key is stored encrypted. Leave blank to remove the key.",
            initialValue: "", masked: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try { await Vm.SaveApiKeyAsync(dlg.Value); }
        catch (Exception ex) { Vm.Status = ex.Message; }
    }
}
