using Hoard.Core.Configuration;
using Hoard.Core.Security;

namespace Hoard.Core.Data.Demo;

/// <summary>
/// Creates a second, self-contained database (default "HoardDemo") with the same schema as the live one and fills it
/// with sample data, granting the existing Hoard application user access so the app can simply be pointed at it.
/// </summary>
public static class DemoDatabaseBuilder
{
    public const string DefaultName = "HoardDemo";

    public static async Task<DemoDataSeeder.SeedSummary> CreateAsync(
        DatabaseSettings live, string appPassword, string adminUser, string adminPassword, string demoDatabaseName,
        ISecretProtector protector, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.Equals(demoDatabaseName, live.DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The demo database must have a different name from the live database.");

        progress?.Report($"Creating database {demoDatabaseName} and granting {live.User} access…");
        await DatabaseProvisioner.ProvisionAsync(live.Host, live.Port, adminUser, adminPassword, demoDatabaseName, live.User, appPassword, ct);

        var demo = new DatabaseSettings { Host = live.Host, Port = live.Port, DatabaseName = demoDatabaseName, User = live.User, UseSsl = live.UseSsl };
        var factory = new MySqlConnectionFactory(SettingsStore.BuildConnectionString(demo, appPassword));

        progress?.Report("Applying schema…");
        await new SchemaMigrator(factory).MigrateAsync(ct);

        progress?.Report("Seeding sample data…");
        return await new DemoDataSeeder(factory, protector).SeedAsync(progress, ct);
    }
}
