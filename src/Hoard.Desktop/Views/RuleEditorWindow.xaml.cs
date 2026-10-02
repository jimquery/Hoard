using System.Windows;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class RuleEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    public RuleEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>Switches the window into "suggest a rule after a manual edit" wording.</summary>
    public void UseSuggestionMode(string categoryPath)
    {
        Title = "Hoard - Always categorize like this?";
        Intro.Text = $"You set this transaction to \"{categoryPath}\". Create a rule so similar transactions are categorized the same way automatically? " +
                     "Adjust the pattern if it is too broad or too narrow.";
        SaveButton.Content = "Create rule & apply";
        CancelButton.Content = "Just this one";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RuleEditorViewModel vm) return;
        await vm.SaveCommand.ExecuteAsync(null);
        if (vm.Saved)
        {
            DialogResult = true;
            Close();
        }
    }
}
