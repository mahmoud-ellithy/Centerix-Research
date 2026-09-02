using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;

using Scalar.AspNetCore;

using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add localization services with resources path
    builder.Services.AddLocalization(options =>
    {
        options.ResourcesPath = "Localization";
    });

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddPresentation(builder.Configuration);

    builder.Host.UseSerilog((context, loggerConfig) =>
        loggerConfig.ReadFrom.Configuration(context.Configuration));

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
        await app.InitialiseDatabaseAsync();
        await app.InitialiseTenantDatabaseAsync();
    }

    app.UseCoreMiddlewares();

    app.MapControllers();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is how EF Core design-time tools (dotnet ef) stop the host after
    // builder.Build(); it must pass through silently. Anything else is a genuine startup failure.
    Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    throw;
}
