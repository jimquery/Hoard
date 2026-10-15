using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Hoard.Desktop.ViewModels;
using Microsoft.Win32;

namespace Hoard.Desktop.Views;

public partial class AdvisorView : UserControl
{
    private bool _loaded;

    public AdvisorView()
    {
        InitializeComponent();
    }

    private AdvisorViewModel Vm => (AdvisorViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        // Keep the newest message in view.
        ((INotifyCollectionChanged)Vm.Messages).CollectionChanged += (_, _) => Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd);
        await Vm.LoadAsync();
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e) => Vm.NewApiKey = ApiKeyBox.Password;

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnQuestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (sender is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await Vm.AskCommand.ExecuteAsync(null);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save advisor conversation",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = $"Hoard advisor {DateTime.Now:yyyy-MM-dd}.md",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, Vm.ExportMarkdown());
    }
}
