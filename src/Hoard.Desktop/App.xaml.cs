using System.IO;
using System.Windows;
using Hoard.Core;
using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Desktop.Services;
using Hoard.Desktop.ViewModels;
using Hoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoard.Desktop;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(Describe(args.Exception), "Hoard - Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var settings = SettingsStore.Load();
        var theme = new ThemeService(settings);
        ChartResources.Register();
        theme.Apply();

        if (!settings.Database.IsConfigured)
        {
            var setup = new SetupWindow { DataContext = new SetupViewModel(settings) };
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            settings = SettingsStore.Load();
            theme = new ThemeService(settings);
            theme.Apply();
        }

        try
        {
            Services = BuildServices(settings, theme);
            await Services.GetRequiredService<SchemaMigrator>().MigrateAsync();
            // Categorize anything that has never been through the rules (e.g. history downloaded before v0.2).
            await Services.GetRequiredService<CategorizationService>().CategorizeNewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Hoard could not open its database.\n\n{Describe(ex)}\n\nCheck that MySQL is running, or delete {SettingsStore.DefaultPath} to run setup again.",
                "Hoard - Database error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var main = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        main.Closed += (_, _) => Shutdown();
        main.Show();
    }

    /// <summary>
    /// The root cause's message (reflection and XAML wrap the real error in TargetInvocationException), with the
    /// full exception written to desktop-error.log next to hoard.json.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var log = Path.Combine(SettingsStore.DefaultDirectory, "desktop-error.log");
        try
        {
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            log = null;
        }
        var root = ex.GetBaseException();
        return log is null ? root.Message : $"{root.Message}\n\nDetails: {log}";
    }

    private static ServiceProvider BuildServices(HoardSettings settings, ThemeService theme)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddHoardCore(settings);
        services.AddSingleton(theme);
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BudgetViewModel>();
        services.AddSingleton<AdvisorViewModel>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        base.OnExit(e);
    }
}
