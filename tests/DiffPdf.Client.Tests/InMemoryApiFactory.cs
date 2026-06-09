using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Boots the API for integration tests with the relational connection strings (and scope-sync root)
/// explicitly cleared, so the in-memory store + in-process Wolverine fallback is always used — regardless
/// of any committed dev configuration (appsettings.Development.json, environment variables). This keeps the
/// integration tests hermetic: dev config can never make them run against a real database.
/// </summary>
public sealed class InMemoryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // A dedicated environment that does not load appsettings.Development.json.
        builder.UseEnvironment("Testing");

        // Last-wins override: force the dev fallback no matter what earlier sources set.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "",
            ["ScopeSync:RootPath"] = "",
            ["ScopeSync:Enabled"] = "false",
            ["Auth:Enabled"] = "false",
            // Push the control-plane's first tick far out so background baseline auto-provisioning never races
            // these deterministic endpoint tests. The branch-create hook runs on the request path regardless.
            ["ControlPlane:TickSeconds"] = "3600",
        }));
    }
}
