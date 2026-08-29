using System.Text;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Infrastructure.Auditing;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Data.Interceptors;
using Centerix.Infrastructure.Email;
using Centerix.Infrastructure.Platform;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Microsoft.AspNetCore.Authorization;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<ICurrentTenant, CurrentTenant>();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        ArgumentNullException.ThrowIfNull(connectionString);

        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, TenantInterceptor>();

        services.AddScoped<IAuditWriter, AuditWriter>();

        // Configure Finbuckle MultiTenant.
        // The tenant is a client SELECTION resolved from the request header or host only. It is
        // never resolved from a token claim: doing so would make the JWT a tenant source of truth,
        // which it must not be (see TenantGuardMiddleware — membership is verified server-side per
        // request). If a tenant switch is needed, the client sends a different `tenant` header on the
        // next request and the guard re-verifies TenantMembership. There is deliberately NO
        // WithClaimStrategy: a `tenant` claim in the JWT must never resolve or authorize a tenant.
        services.AddMultiTenant<CenterixTenantInfo>()
            .WithHeaderStrategy(TenancyConstants.TenantIdName)
            .WithHostStrategy("tenant") // e.g., tenant1.myapp.com
            .WithEFCoreStore<TenantDbContext, CenterixTenantInfo>();

        // Tenant registry context — uses its own migrations history table
        // so it doesn't conflict with AppDbContext in the shared database.
        // NOTE: This MUST come AFTER WithEFCoreStore so that our explicit AddDbContext
        // overrides the parameterless AddDbContext<TenantDbContext>() called internally
        // by WithEFCoreStore. This ensures only one database provider is configured.
        services.AddDbContext<TenantDbContext>(options =>
            options.UseSqlServer(connectionString,
                sql => sql.MigrationsHistoryTable("__TenantMigrationsHistory")));

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.UseSqlServer(connectionString);
        });

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddScoped<ApplicationDbContextInitialiser>();
        services.AddTransient<ITenantDbSeeder, TenantDbSeeder>();
        services.AddScoped<ITenantRegistrySync, TenantRegistrySyncService>();
        services.AddTransient<IPlatformService, PlatformService>();

        // Identity configuration
        services
        .AddIdentityCore<IdentityUser>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequiredUniqueChars = 2;
            options.SignIn.RequireConfirmedAccount = false;
            options.Lockout.MaxFailedAccessAttempts = 10;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;
        })
        .AddRoles<ApplicationRole>()
        .AddEntityFrameworkStores<AppDbContext>();

        // JWT Authentication
        services.AddAuthorization();

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(options =>
        {
            var jwtSettings = configuration.GetSection("JwtSettings");

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings["Issuer"],
                ValidAudience = jwtSettings["Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(
                       Encoding.UTF8.GetBytes(jwtSettings["Secret"]!)),
            };
        });

        // HybridCache
        // Permission-based authorization
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, FeatureAuthorizationHandler>();

        // Phase 2: subscription state, feature entitlement and plan-limit enforcement
        services.AddScoped<ISubscriptionStateService, SubscriptionStateService>();
        services.AddScoped<IFeatureAccessService, FeatureAccessService>();
        services.AddScoped<ILimitService, LimitService>();
        services.AddScoped<IPlatformAdminGuard, PlatformAdminGuard>();
        services.AddScoped<ISubscriptionFactory, SubscriptionFactory>();

        // JWT settings (strongly-typed) with startup validation
        services.Configure<JwtSettings>(configuration.GetSection("JwtSettings"));
        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection("JwtSettings"))
            .Validate(settings =>
            {
                settings.Validate();
                return true;
            })
            .ValidateOnStart();

        // Token generation service
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // Tenant-scoped permission resolver
        services.AddScoped<ITenantPermissionResolver, TenantPermissionResolver>();

        // Identity service abstraction
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IRoleService, RoleService>();

        // Email sender (development mode: logs to console)
        services.AddScoped<IEmailSender, DevelopmentEmailSender>();

        // Invitation links: environment-specific base URL, validated at startup (fail fast).
        services.Configure<InvitationLinkOptions>(configuration.GetSection(InvitationLinkOptions.SectionName));
        services.AddOptions<InvitationLinkOptions>()
            .Bind(configuration.GetSection(InvitationLinkOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp),
                $"Invitations:BaseUrl must be an absolute http(s) URL pointing at the application front end. " +
                "Configure it per environment before starting the API.")
            .ValidateOnStart();
        services.AddScoped<IInvitationLinkBuilder, InvitationLinkBuilder>();
        services.AddHybridCache();

        return services;
    }
}