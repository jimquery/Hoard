using System.Windows;
using System.Windows.Controls;
using Hoard.Desktop.Services;
using Hoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Hoard.Desktop.Views;

/// <summary>
/// The shell: title bar, the navigation rail and one page at a time. Every command lives on the page it belongs to
/// (Institutions, Accounts, Transactions, Settings); the window only routes navigation and the events that need a
/// dialog owner.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _syncingRail;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<ThemeService>().WatchSystem(this);
        InstitutionsPage.DataContext = Vm;
        AccountsPage.DataContext = Vm;
        TransactionsPage.DataContext = Vm;
        Vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.CurrentPage)) ShowPage(Vm.CurrentPage); };
        Vm.RuleSuggestionHandler = SuggestRuleAsync;
        // A desktop-initiated sync that flagged probable duplicates opens the review dialog straight away; background
        // (service) syncs only flag, and the Accounts page shows the notice on the next refresh.
        Vm.DuplicatesDetected += (_, _) => OpenMergeDuplicates();
        await Vm.RefreshAsync(); // also loads the dashboard via MarkDataChanged()
    }

    // ----- navigation -----

    private void OnRailChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent (SelectedIndex=0) before the DataContext exists; ignore that.
        if (_syncingRail || DataContext is not MainViewModel vm || Rail.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<AppPage>(tag, out var page)) vm.CurrentPage = page;
    }

    private void ShowPage(AppPage page)
    {
        DashboardPage.Visibility = page == AppPage.Dashboard ? Visibility.Visible : Visibility.Collapsed;
        InstitutionsPage.Visibility = page == AppPage.Institutions ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == AppPage.Accounts ? Visibility.Visible : Visibility.Collapsed;
        TransactionsPage.Visibility = page == AppPage.Transactions ? Visibility.Visible : Visibility.Collapsed;
        ReportsPage.Visibility = page == AppPage.Reports ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        _syncingRail = true;
        try
        {
            foreach (var item in Rail.Items.OfType<ListBoxItem>())
                if (item.Tag as string == page.ToString()) { Rail.SelectedItem = item; break; }
        }
        finally { _syncingRail = false; }
    }

    // ----- dialogs the window itself owns -----

    private async void OpenMergeDuplicates()
    {
        Vm.CurrentPage = AppPage.Accounts;
        var vm = new MergeAccountsViewModel(Vm.MergeService, Vm.AccountRepository);
        var dlg = new MergeAccountsWindow { Owner = this, DataContext = vm };
        dlg.ShowDialog();
        if (vm.Changed)
        {
            await Vm.RefreshAsync();
            if (!string.IsNullOrEmpty(vm.Status)) Vm.StatusMessage = vm.Status;
        }
    }

    /// <summary>After a manual re-categorization, offer to turn it into a rule.</summary>
    private async Task SuggestRuleAsync(TransactionItem item, CategoryOption category)
    {
        var accounts = Vm.Accounts.Select(a => new AccountOption(a.Id, $"{a.InstitutionName} - {a.Name}"));
        var editor = new RuleEditorViewModel(Vm.Categorization, Vm.CategoryRepository, Vm.TransactionCategories, accounts,
            existing: null, sampleDescription: item.Description, samplePayee: item.Payee);
        editor.SelectedCategory = editor.CategoryOptions.FirstOrDefault(c => c.Id == category.Id);

        var dlg = new RuleEditorWindow { Owner = this, DataContext = editor };
        dlg.UseSuggestionMode(category.Path);
        if (dlg.ShowDialog() == true)
        {
            Vm.StatusMessage = editor.Status;
            await Vm.LoadTransactionsAsync();
            Vm.MarkDataChanged();
        }
    }
}
