using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.Extensions.Options;

using Scalar.AspNetCore;

using Serilog;

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

app.Run();
