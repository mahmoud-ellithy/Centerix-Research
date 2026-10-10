using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Email;
using Centerix.Infrastructure.Tenancy;

using Microsoft.Extensions.Options;

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

    // REV-012: EmailOptions.Validate runs first so Capture-mode misuse outside
    // Development/Testing is rejected before any side-effecting startup work.
    var emailOptions = app.Services.GetRequiredService<IOptions<EmailOptions>>().Value;
    emailOptions.Validate(app.Environment);

    // CFG-001 + REV-012: deterministic SMTP gate when the effective email mode is Smtp.
    // The previous code branched on Production only; Capture mode legitimately skips this
    // check, so the trigger is now the resolved email mode rather than the environment name.
    if (emailOptions.ResolveMode(app.Environment) == EmailMode.Smtp)
    {
        var smtp = app.Services.GetRequiredService<IOptions<SmtpOptions>>().Value;
        smtp.Validate();
    }

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    // NEW-2: configured database initialization in every environment except Testing
    // (the test host manages its own stores). Honors DatabaseInitialization:
    // ApplyMigrations / Seed / ValidateSchema with tenant-registry-first ordering.
    // Replaces the previous Development-only InitialiseDatabaseAsync calls.
    await app.RunDatabaseInitializationAsync();

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
