using System.IO;
using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class ImportWindow : Wpf.Ui.Controls.FluentWindow
{
    public ImportWindow()
    {
        InitializeComponent();
    }

    private ImportViewModel Vm => (ImportViewModel)DataContext;

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the exported transactions file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(Vm.FilePath) && Directory.Exists(Path.GetDirectoryName(Vm.FilePath)))
            dlg.InitialDirectory = Path.GetDirectoryName(Vm.FilePath);
        if (dlg.ShowDialog(this) == true) await Vm.LoadFileAsync(dlg.FileName);
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Vm.FilePath)) { OnBrowseClick(sender, e); return; }
        if (!File.Exists(Vm.FilePath)) { Vm.Status = "File not found: " + Vm.FilePath; return; }
        await Vm.LoadFileAsync(Vm.FilePath);
    }
}
