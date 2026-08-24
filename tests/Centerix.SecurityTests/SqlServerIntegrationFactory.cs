using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Data.Interceptors;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Owns a REAL SQL Server database used by the relational integration suite.
/// Connection resolution order:
///   1. CENTERIX_SQLTEST_CONNECTION environment variable (explicit external server),
///   2. a local SQL Server instance ("Server=.") when reachable,
///   3. an ephemeral Testcontainers MsSql container (CI).
/// A uniquely named database is created for the run and dropped afterwards.
/// </summary>
public sealed class SqlServerDatabaseFixture : IAsyncLifetime
{
    public const string ExternalConnectionEnvVar = "CENTERIX_SQLTEST_CONNECTION";

    private const string LocalMasterConnectionString =
        "Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;Connect Timeout=5";

    private MsSqlContainer? _container;

    /// <summary>Master connection string (no database selected).</summary>
    public string MasterConnectionString { get; private set; } = string.Empty;

    /// <summary>Full connection string including the unique test database.</summary>
    public string ConnectionString => $"{MasterConnectionString};Database={DatabaseName}";

    public string DatabaseName { get; private set; } = $"CenterixSec_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        MasterConnectionString = await ResolveMasterConnectionStringAsync();
        Console.WriteLine($"[SqlServerFixture] Using master connection: {Masked(MasterConnectionString)}");

        // Create the isolated test database through EF's own SQL client stack.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(MasterConnectionString)
            .Options;
        await using (var context = new TenantDbContext(options))
        {
            await context.Database.ExecuteSqlRawAsync($"CREATE DATABASE [{DatabaseName}]");
        }
        Console.WriteLine($"[SqlServerFixture] Created database {DatabaseName}");

        // Disable pooling so the database can be dropped immediately on dispose.
        MasterConnectionString += ";Pooling=false";
    }

    private static string Masked(string connectionString)
        => System.Text.RegularExpressions.Regex.Replace(
            connectionString, @"(Password|PWD)=([^;]*)", "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public async Task DisposeAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(MasterConnectionString)
                .Options;
            await using (var context = new TenantDbContext(options))
            {
                await context.Database.ExecuteSqlRawAsync(
                    $"IF DB_ID('{DatabaseName}') IS NOT NULL ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                await context.Database.ExecuteSqlRawAsync(
                    $"IF DB_ID('{DatabaseName}') IS NOT NULL DROP DATABASE [{DatabaseName}]");
            }
        }
        catch
        {
            // Best-effort cleanup; a leaked uniquely-named database never affects other runs.
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task<string> ResolveMasterConnectionStringAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalConnectionEnvVar);
        if (!string.IsNullOrWhiteSpace(external))
        {
            Console.WriteLine($"[SqlServerFixture] Using external connection from {ExternalConnectionEnvVar}");
            return external.TrimEnd(';');
        }

        Console.WriteLine("[SqlServerFixture] Probing local SQL Server (Server=.)...");
        if (await CanReachAsync(LocalMasterConnectionString))
        {
            Console.WriteLine("[SqlServerFixture] Local SQL Server reachable.");
            return LocalMasterConnectionString;
        }

        Console.WriteLine("[SqlServerFixture] Local SQL Server unreachable; starting Testcontainers MsSql...");
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
        await _container.StartAsync();
        var containerConnectionString = _container.GetConnectionString();
        if (!containerConnectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        {
            containerConnectionString += ";TrustServerCertificate=True";
        }

        return containerConnectionString;
    }

    private static async Task<bool> CanReachAsync(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            await using var context = new TenantDbContext(options);
            return await context.Database.CanConnectAsync();
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// <see cref="TestWebApplicationFactory"/> variant targeting real SQL Server: applies actual
/// migrations, registers production interceptors and uses the production history-table split.
/// </summary>
public sealed class SqlServerWebApplicationFactory(string masterConnectionString) : TestWebApplicationFactory
{
    private readonly string _connectionString = masterConnectionString;

    protected override void ConfigureAppDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options
            .UseSqlServer(_connectionString)
            .AddInterceptors(services.GetServices<ISaveChangesInterceptor>());

    protected override void ConfigureTenantDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options.UseSqlServer(_connectionString,
            sql => sql.MigrationsHistoryTable("__TenantMigrationsHistory"));
}

/// <summary>
/// Collection fixture sharing ONE migrated SQL Server database and ONE booted HTTP host across all
/// relational integration test classes, so the migration chain executes exactly once per run.
/// </summary>
[CollectionDefinition("SqlServerIntegration")]
public sealed class SqlServerIntegrationCollection : ICollectionFixture<SqlServerIntegrationFactory>;

public sealed class SqlServerIntegrationFactory : IAsyncLifetime
{
    private readonly SqlServerDatabaseFixture _database = new();

    public SqlServerWebApplicationFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public CapturingEmailSender EmailSender => Factory.EmailSender;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        Console.WriteLine($"[SqlServerFixture] Using database {_database.DatabaseName}");

        Factory = new SqlServerWebApplicationFactory(_database.ConnectionString);
        Client = Factory.CreateClient();
        Console.WriteLine("[SqlServerFixture] Test host built.");

        // Apply the real migration chain for BOTH contexts before any request touches the
        // database. TenantDbContext goes FIRST: AppDbContext's AddTenantMemberships migration
        // creates a raw-SQL FK referencing Platform.TenantRegistry.
        using (var scope = Factory.Services.CreateScope())
        {
            var tenantDbContext = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            await tenantDbContext.Database.MigrateAsync();
            Console.WriteLine("[SqlServerFixture] TenantDbContext migrated.");
        }

        using (var scope = Factory.Services.CreateScope())
        {
            Console.WriteLine("[SqlServerFixture] AppDbContext scope created.");
            var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Console.WriteLine("[SqlServerFixture] AppDbContext resolved.");
            var canConnect = await appDbContext.Database.CanConnectAsync();
            Console.WriteLine($"[SqlServerFixture] CanConnect={canConnect}");
            var pending = await appDbContext.Database.GetPendingMigrationsAsync();
            Console.WriteLine($"[SqlServerFixture] {pending.Count()} pending migrations.");
            foreach (var migration in pending)
            {
                Console.WriteLine($"[SqlServerFixture] Applying {migration}...");
                await appDbContext.Database.MigrateAsync(migration);
                Console.WriteLine($"[SqlServerFixture] Applied {migration}.");
            }
            Console.WriteLine("[SqlServerFixture] AppDbContext migrated.");
        }
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        await _database.DisposeAsync();
    }
}
