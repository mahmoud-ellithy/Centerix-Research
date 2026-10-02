using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore.Stores.EFCoreStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
/// migrations and uses the production history-table split. The multi-tenant store is already
/// the production EFCoreStore (inherited from the base factory).
/// </summary>
public sealed class SqlServerWebApplicationFactory(string masterConnectionString) : TestWebApplicationFactory
{
    private readonly string _connectionString = masterConnectionString;

    // NOTE: must NOT resolve services (e.g. SaveChanges interceptors) inside this callback.
    // DbContextOptions<AppDbContext> is a singleton constructed ON DEMAND while the container
    // is already mid-resolution of that very call site; re-entering the container here creates
    // a circular dependency that deadlocks the resolver (verified via dotnet-stack).
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Replace the production CurrentTenant with the AsyncLocal-backed fake so that
            // per-test SetTenantId() calls are visible to all handler/scoped-service resolutions
            // within the same async flow (ICurrentTenant is resolved as a singleton-scoped service
            // by the handler's DI scope, but it reads from AsyncLocal which flows with the
            // ExecutionContext — the same flow that SetTenantId() writes to).
            //
            // The fake is also wired to Finbuckle's IMultiTenantContextAccessor by the fixture's
            // InitializeAsync (after the host is built) so HTTP-driven tests that deliver the
            // tenant via the `tenant` request header see the same resolved tenant production does.
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentTenant));
            if (existing is not null) services.Remove(existing);
            services.AddSingleton<TaskCFakeCurrentTenant>();
            services.AddSingleton<ICurrentTenant>(sp => sp.GetRequiredService<TaskCFakeCurrentTenant>());
        });
    }

    protected override void ConfigureAppDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options.UseSqlServer(_connectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));

    protected override void ConfigureTenantDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options.UseSqlServer(_connectionString,
            sql => sql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}

/// <summary>
/// Collection fixture sharing ONE migrated SQL Server database and ONE booted HTTP host across all
/// relational integration test classes, so the migration chain executes exactly once per run.
/// DisableParallelization keeps the real-SQL-Server workloads from contending with the
/// InMemory suites for connection-pool/server capacity (observed login timeouts otherwise).
/// </summary>
[CollectionDefinition("SqlServerIntegration", DisableParallelization = true)]
public sealed class SqlServerIntegrationCollection : ICollectionFixture<SqlServerIntegrationFactory>;

public sealed class SqlServerIntegrationFactory : IAsyncLifetime
{
    private static readonly string DiagnosticsLogPath =
        Path.Combine(Path.GetTempPath(), "centerix-sqltest.log");

    private readonly SqlServerDatabaseFixture _database = new();

    public SqlServerWebApplicationFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public CapturingEmailSender EmailSender => Factory.EmailSender;

    /// <summary>
    /// Console output from xUnit fixtures can stay buffered under VSTest, so every stage is
    /// mirrored into %TEMP%\centerix-sqltest.log (flushed immediately) for diagnosis.
    /// </summary>
    internal static void Log(string message)
    {
        Console.WriteLine(message);
        try
        {
            File.AppendAllText(DiagnosticsLogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only; never fail the suite over log IO.
        }
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        Log($"[SqlServerFixture] Using database {_database.DatabaseName}");

        Factory = new SqlServerWebApplicationFactory(_database.ConnectionString);
        Client = Factory.CreateClient();
        Log("[SqlServerFixture] Test host built.");

        // Wire the Finbuckle accessor into the fake tenant so HTTP-driven tests (which deliver
        // the tenant via the `tenant` request header resolved by Finbuckle) see the resolved
        // tenant through ICurrentTenant — matching the production CurrentTenant implementation.
        Factory.Services.GetRequiredService<TaskCFakeCurrentTenant>()
            .SetMultiTenantContextAccessor(
                Factory.Services.GetRequiredService<IMultiTenantContextAccessor<CenterixTenantInfo>>());
        Log("[SqlServerFixture] Tenant wiring complete.");

        // Apply the real migration chain for BOTH contexts before any request touches the
        // database. TenantDbContext goes FIRST: AppDbContext's AddTenantMemberships migration
        // creates a raw-SQL FK referencing Platform.TenantRegistry. Both contexts are built
        // standalone (the IDesignTimeDbContextFactory pattern) so migration bootstrap never
        // resolves runtime-only services (IMediator/ICurrentTenant) from the host container,
        // and the history-table split matches production configuration.
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(_database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        await using (var tenantDbContext = new TenantDbContext(tenantOptions))
        {
            await tenantDbContext.Database.MigrateAsync();
        }
        Log("[SqlServerFixture] TenantDbContext migrated.");

        var appOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_database.ConnectionString)
            .Options;
        await using (var appDbContext = new AppDbContext(appOptions, null!, null!))
        {
            var canConnect = await appDbContext.Database.CanConnectAsync();
            Log($"[SqlServerFixture] AppDbContext CanConnect={canConnect}");
            var pending = await appDbContext.Database.GetPendingMigrationsAsync();
            Log($"[SqlServerFixture] {pending.Count()} pending AppDbContext migrations.");
            await appDbContext.Database.MigrateAsync();
            Log("[SqlServerFixture] AppDbContext migrated.");
        }
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        await _database.DisposeAsync();
    }
}

/// <summary>
/// Per-async-flow fake tenant using AsyncLocal so each test can set its own tenant context
/// without affecting sibling tests sharing the same singleton instance. Also exposes the
/// production <c>CurrentTenant</c> field shape (<c>_authorizedTenantId</c>, <c>_isAuthorized</c>)
/// so legacy SQL-integration tests that set them via reflection keep working.
///
/// When a Finbuckle <see cref="IMultiTenantContextAccessor{T}"/> is available (HTTP-test path),
/// the resolved tenant flows through it the same way the production <c>CurrentTenant</c> reads it,
/// so <see cref="TenantGuardMiddleware"/> sees the right context for membership verification.
/// Defaults to <c>tenant-freemonths-c</c> for backward compatibility with non-HTTP tests.
/// </summary>
internal class TaskCFakeCurrentTenant : ICurrentTenant
{
    private static readonly AsyncLocal<string?> _asyncLocalTenantId = new();
    private const string DefaultTenantId = "tenant-freemonths-c";

    // Mirrors the field shape of the production CurrentTenant so legacy tests that
    // configure them via reflection (BindingFlags.NonPublic | BindingFlags.Instance)
    // continue to work after the SqlServer fixture swaps in this fake.
    private string? _authorizedTenantId;
    private bool _isAuthorized;

    // Optional reference to Finbuckle's context accessor so HTTP-driven tests that
    // send a `tenant` request header see the resolved tenant through ICurrentTenant.
    private IMultiTenantContextAccessor<CenterixTenantInfo>? _multiTenantContextAccessor;

    public string TenantId =>
        _isAuthorized && _authorizedTenantId is not null
            ? _authorizedTenantId
            : (_asyncLocalTenantId.Value ?? DefaultTenantId);

    public string ResolvedTenantId =>
        _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id
        ?? _asyncLocalTenantId.Value
        ?? DefaultTenantId;

    public bool IsAuthorized => _isAuthorized || _asyncLocalTenantId.Value is not null;

    public bool IsResolved =>
        (_multiTenantContextAccessor?.MultiTenantContext?.TenantInfo != null)
        || _asyncLocalTenantId.Value is not null;

    public bool IsActive =>
        _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.IsActive ?? true;

    public DateTime? ValidUpTo
    {
        get
        {
            var validUpTo = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.ValidUpTo;
            return validUpTo == DateTime.MinValue ? null : validUpTo;
        }
    }

    public void AuthorizeTenant()
    {
        _authorizedTenantId = ResolvedTenantId;
        _isAuthorized = true;
    }

    /// <summary>Wires the Finbuckle accessor for HTTP-driven tenant resolution.</summary>
    public void SetMultiTenantContextAccessor(IMultiTenantContextAccessor<CenterixTenantInfo> accessor)
        => _multiTenantContextAccessor = accessor;

    /// <summary>Sets the per-async-flow tenant ID. Intended for test setup only.</summary>
    public static void SetTenantId(string tenantId) => _asyncLocalTenantId.Value = tenantId;

    /// <summary>Resets to the default tenant. Intended for test cleanup.</summary>
    public static void ResetTenantId() => _asyncLocalTenantId.Value = null;
}
