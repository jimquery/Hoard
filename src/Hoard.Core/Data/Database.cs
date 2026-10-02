using System.Reflection;
using Dapper;
using MySqlConnector;

namespace Hoard.Core.Data;

public interface IDbConnectionFactory
{
    Task<MySqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class MySqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    static MySqlConnectionFactory()
    {
        // Map snake_case columns to PascalCase properties.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public MySqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// Applies embedded SQL migration scripts (Data/Migrations/NNNN_name.sql) in order, recording each in
/// schema_migrations. Scripts must be idempotent-safe or guarded; each runs inside a transaction where MySQL allows.
/// </summary>
public sealed class SchemaMigrator
{
    private readonly IDbConnectionFactory _factory;

    public SchemaMigrator(IDbConnectionFactory factory) => _factory = factory;

    public static IReadOnlyList<(string Name, string Sql)> LoadEmbeddedScripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                // Resource names look like Hoard.Core.Data.Migrations.0001_initial.sql
                var name = n[(n.IndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
                return (name, r.ReadToEnd());
            })
            .ToList();
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name           VARCHAR(200) NOT NULL,
                applied_at_utc DATETIME(6)  NOT NULL,
                PRIMARY KEY (name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """);
        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations")).ToHashSet(StringComparer.Ordinal);
        var newlyApplied = new List<string>();
        foreach (var (name, sql) in LoadEmbeddedScripts())
        {
            if (applied.Contains(name)) continue;
            await conn.ExecuteAsync(sql);
            await conn.ExecuteAsync(
                "INSERT INTO schema_migrations (name, applied_at_utc) VALUES (@name, @now)",
                new { name, now = DateTime.UtcNow });
            newlyApplied.Add(name);
        }
        return newlyApplied;
    }
}

/// <summary>Creates the Hoard database and a least-privilege user using an administrative login. Used by first-run setup.</summary>
public static class DatabaseProvisioner
{
    public static async Task ProvisionAsync(
        string host, int port, string adminUser, string adminPassword,
        string databaseName, string appUser, string appPassword, CancellationToken ct = default)
    {
        var csb = new MySqlConnectionStringBuilder
        {
            Server = host, Port = (uint)port, UserID = adminUser, Password = adminPassword, SslMode = MySqlSslMode.Preferred,
        };
        using var conn = new MySqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        var db = MySqlHelper.EscapeString(databaseName);
        var user = MySqlHelper.EscapeString(appUser);
        var pwd = MySqlHelper.EscapeString(appPassword);
        await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        await conn.ExecuteAsync($"CREATE USER IF NOT EXISTS '{user}'@'localhost' IDENTIFIED BY '{pwd}'");
        await conn.ExecuteAsync($"ALTER USER '{user}'@'localhost' IDENTIFIED BY '{pwd}'");
        await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES, DROP ON `{db}`.* TO '{user}'@'localhost'");
        await conn.ExecuteAsync("FLUSH PRIVILEGES");
    }

    public static async Task<string> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>("SELECT VERSION()") ?? "unknown";
    }
}
