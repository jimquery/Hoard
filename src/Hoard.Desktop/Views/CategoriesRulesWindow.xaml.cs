using System.Windows;
using System.Windows.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class CategoriesRulesWindow : Wpf.Ui.Controls.FluentWindow
{
    public CategoriesRulesWindow()
    {
        InitializeComponent();
    }

    private CategoriesRulesViewModel Vm => (CategoriesRulesViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private async void OnNewRuleClick(object sender, RoutedEventArgs e) => await EditRuleAsync(null);

    private async void OnEditRuleClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedRule is { } r) await EditRuleAsync(Vm.ToRule(r));
    }

    private async void OnRuleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedRule is { } r) await EditRuleAsync(Vm.ToRule(r));
    }

    private async Task EditRuleAsync(Core.Domain.CategoryRule? existing)
    {
        var dlg = new RuleEditorWindow
        {
            Owner = this,
            DataContext = new RuleEditorViewModel(Vm.Categorization, Vm.Repository, Vm.CategoryOptions, Vm.AccountOptions, existing),
        };
        if (dlg.ShowDialog() == true)
        {
            Vm.Changed = true;
            Vm.Status = ((RuleEditorViewModel)dlg.DataContext).Status;
            await Vm.ReloadRulesAsync();
        }
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedRule is null) return;
        var answer = MessageBox.Show(this, $"Delete the rule \"{Vm.SelectedRule.Pattern}\"?", "Hoard", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.DeleteRuleCommand.ExecuteAsync(null);
    }

    private async void OnDeleteCategoryClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCategory is null) return;
        var c = Vm.SelectedCategory;
        var scope = c.ParentId is null ? " and every sub-category under it" : "";
        var answer = MessageBox.Show(this,
            $"Delete \"{c.Path}\"{scope}?\n\nTransactions in it become uncategorized and rules pointing to it are removed.",
            "Hoard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await Vm.DeleteCategoryCommand.ExecuteAsync(null);
    }
}
