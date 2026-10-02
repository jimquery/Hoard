using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class ReportsView : UserControl
{
    public ReportsView()
    {
        InitializeComponent();
    }

    // Planned monthly contribution cells (College savings): commit when the cell loses focus or Enter is pressed.
    private async void OnPlannedLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: CollegeRow row }) await row.CommitPlannedAsync();
    }

    private async void OnPlannedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: CollegeRow row } box) return;
        e.Handled = true;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await row.CommitPlannedAsync();
    }
}
