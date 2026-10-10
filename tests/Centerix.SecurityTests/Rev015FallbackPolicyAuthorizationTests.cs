using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Centerix.SecurityTests;

// REV-015: PermissionPolicyProvider.GetFallbackPolicyAsync previously returned null,
// silently disabling the SetFallbackPolicy(RequireAuthenticatedUser) registered in
// API/DependencyInjection.cs. An unattributed endpoint would then be reachable
// anonymously. These tests prove the fallback is now active and that no real
// controller action would have relied on the (broken) null result.
public class Rev015FallbackPolicyAuthorizationTests
{
    // ================================================================
    // 1. Reflection: every action is attribute-bearing.
    // ================================================================

    [Fact]
    public void EveryControllerAction_CarriesAuthAttribute()
    {
        var apiAssembly = typeof(Centerix.API.Controllers.ApiController).Assembly;
        var controllers = apiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.FullName?.StartsWith("Centerix.API.Controllers", StringComparison.Ordinal) == true)
            .ToList();

        var offenders = new List<string>();

        foreach (var controller in controllers)
        {
            var typeHasAuthorize = HasAttribute<HasPermissionAttribute>(controller)
                                   || HasAttribute<AuthorizeAttribute>(controller);

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsActionMethod(action))
                {
                    continue;
                }

                var actionHasAttribute =
                    HasAttribute<HasPermissionAttribute>(action)
                    || HasAttribute<AuthorizeAttribute>(action)
                    || HasAttribute<AllowAnonymousAttribute>(action);

                var controllerAllowsAnon = HasAttribute<AllowAnonymousAttribute>(controller);

                if (!actionHasAttribute && !typeHasAuthorize && !controllerAllowsAnon)
                {
                    offenders.Add(
                        $"{controller.FullName}.{action.Name} has no authorization attribute " +
                        "(HasPermission/Authorize/AllowAnonymous); the fallback policy requires authentication.");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The following actions lack any authorization attribute and would be reachable " +
            "anonymously only because the fallback policy was previously disabled:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ================================================================
    // 2. Integration: an un-attributed endpoint returns 401.
    // ================================================================

    [Fact]
    public async Task UnattributedEndpoint_Returns401_WhenUnauthenticated()
    {
        using var host = FallbackPolicyTestHost.Start();

        using var client = host.CreateClient();
        var response = await client.GetAsync("/rev015/anonymous");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymousEndpoint_Returns200_WhenUnauthenticated()
    {
        using var host = FallbackPolicyTestHost.Start();

        using var client = host.CreateClient();
        var response = await client.GetAsync("/rev015/explicit-anonymous");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static bool IsActionMethod(MethodInfo method)
    {
        if (method.DeclaringType == typeof(object))
        {
            return false;
        }

        // Skip inherited scaffolding (Ok, NotFound, File, ...) inherited from ControllerBase
        // or its parents; only the user's own action methods count.
        if (method.DeclaringType is null || method.DeclaringType.FullName == "Microsoft.AspNetCore.Mvc.ControllerBase")
        {
            return false;
        }

        if (method.GetCustomAttributes().Any(a =>
            a.GetType().FullName?.StartsWith("Microsoft.AspNetCore.Mvc.", StringComparison.Ordinal) == true))
        {
            return true;
        }

        return method.Name.EndsWith("Action", StringComparison.Ordinal)
               || method.Name.EndsWith("Async", StringComparison.Ordinal);
    }

    private static bool HasAttribute<TAttribute>(MemberInfo member) where TAttribute : Attribute
        => member.GetCustomAttributes(typeof(TAttribute), inherit: true).Any();

    private sealed class FallbackPolicyTestHost : IDisposable, IAsyncDisposable
    {
        private TestServer? _server;

        private FallbackPolicyTestHost(TestServer server) => _server = server;

        public static FallbackPolicyTestHost Start()
        {
            var builder = new WebHostBuilder()
                .UseEnvironment("Testing")
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(o =>
                        o.DefaultAuthenticateScheme = "Rev015")
                        .AddScheme<AuthenticationSchemeOptions, Rev015AlwaysAnonymousHandler>("Rev015", _ => { });
                    services.AddAuthorization(options =>
                    {
                        options.FallbackPolicy = new AuthorizationPolicyBuilder()
                            .RequireAuthenticatedUser()
                            .Build();
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/rev015/anonymous", () => Results.Text("ok"));
                        endpoints.MapGet("/rev015/explicit-anonymous",
                            [AllowAnonymous] () => Results.Text("ok"));
                    });
                });

            var server = new TestServer(builder);
            return new FallbackPolicyTestHost(server);
        }

        public HttpClient CreateClient() => _server!.CreateClient();

        public void Dispose()
        {
            _server?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // Authentication handler that leaves the request anonymous. REV-015 tests are
    // exercising the framework's fallback policy: the unauthenticated path must hit
    // 401, the [AllowAnonymous] path must hit 200. We never mint a successful identity
    // here because that would mask the very auth gap the item is fixing.
    private sealed class Rev015AlwaysAnonymousHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}
