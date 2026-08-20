using System.Text;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Centerix.SecurityTests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"SecurityTestDb_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Secret"] = "ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!",
                    ["JwtSettings:Issuer"] = "TestIssuer",
                    ["JwtSettings:Audience"] = "TestAudience",
                    ["JwtSettings:ExpirationInMinutes"] = "60",
                    ["ConnectionStrings:DefaultConnection"] = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;"
                })
                .Build();

            config.AddConfiguration(testConfig);
        });

        builder.ConfigureServices(services =>
        {
            // Replace AppDbContext with InMemory
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });

            // Replace TenantDbContext with InMemory
            var tenantDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<TenantDbContext>));
            if (tenantDescriptor != null)
            {
                services.Remove(tenantDescriptor);
            }

            services.AddDbContext<TenantDbContext>(options =>
            {
                options.UseInMemoryDatabase($"{_databaseName}_Tenant");
            });
        });
    }

    public string GenerateTestToken(string userId, string email, IList<string> roles, IList<string> permissions)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new(System.Security.Claims.ClaimTypes.Name, email),
            new(System.Security.Claims.ClaimTypes.Email, email),
        };

        foreach (var role in roles)
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));

        foreach (var permission in permissions)
            claims.Add(new System.Security.Claims.Claim(Permissions.ClaimType, permission));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "TestIssuer",
            audience: "TestAudience",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
