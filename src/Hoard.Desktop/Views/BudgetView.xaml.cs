using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class BudgetView : UserControl
{
    public BudgetView()
    {
        InitializeComponent();
    }

    private BudgetViewModel Vm => (BudgetViewModel)DataContext;

    // Target cells commit when the cell loses focus or Enter is pressed.
    private async void OnTargetLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: BudgetRowItem row }) await row.CommitTargetAsync();
    }

    private async void OnTargetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: BudgetRowItem row } box) return;
        e.Handled = true;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await row.CommitTargetAsync();
    }

    private async void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Replace every category group's target with its suggestion from {Vm.SelectedMonth.Label} on?\n\nTargets for earlier months are not changed.",
            "Hoard - Reset budget targets", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.ResetAllToSuggestedAsync();
    }
}
