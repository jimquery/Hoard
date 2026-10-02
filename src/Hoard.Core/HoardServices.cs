using Hoard.Core.Categorization;
using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Providers;
using Hoard.Core.Providers.RealEstate;
using Hoard.Core.Providers.SimpleFin;
using Hoard.Core.Reporting;
using Hoard.Core.Security;
using Hoard.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hoard.Core;

/// <summary>Registers everything in Hoard.Core. Used by both Hoard.Desktop and Hoard.Service.</summary>
public static class HoardServices
{
    public static IServiceCollection AddHoardCore(this IServiceCollection services, HoardSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(settings.Sync);

        services.TryAddSingleton<ISecretProtector>(_ =>
            OperatingSystem.IsWindows()
                ? DpapiSecretProtector.FromScopeName(settings.Security.ProtectionScope)
                : new InMemorySecretProtector());

        services.AddSingleton<IDbConnectionFactory>(sp =>
            new MySqlConnectionFactory(SettingsStore.BuildConnectionString(settings.Database, sp.GetRequiredService<ISecretProtector>())));

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<IDataKeyStore, DataKeyStore>();
        services.AddSingleton<IInstitutionRepository, InstitutionRepository>();
        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<ITransactionRepository, TransactionRepository>();
        services.AddSingleton<ISyncRunRepository, SyncRunRepository>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<IHouseholdRepository, HouseholdRepository>();
        services.AddSingleton<IImportRepository, ImportRepository>();
        services.AddSingleton<Import.TransactionImportService>();
        services.AddSingleton<CategorizationService>();
        services.AddSingleton<IReportRepository, ReportRepository>();
        services.AddSingleton<ReportingService>();
        services.AddSingleton<CredentialVault>();

        services.AddSingleton(sp =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Hoard/0.1 (+personal finance aggregator)");
            return new SimpleFinClient(http);
        });
        services.AddSingleton<IFinancialProvider, SimpleFinProvider>();

        services.AddSingleton<IPropertyRepository, PropertyRepository>();
        services.AddSingleton<IPropertyValueProvider>(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Hoard/0.6 (+personal finance aggregator)");
            return new RentCastClient(http);
        });
        services.AddSingleton<RealEstateProvider>();
        services.AddSingleton<IFinancialProvider>(sp => sp.GetRequiredService<RealEstateProvider>());
        services.AddSingleton<PropertyService>();

        services.AddSingleton<InstitutionService>();
        services.AddSingleton<AccountMergeService>();
        services.AddSingleton<SyncService>();
        return services;
    }
}
