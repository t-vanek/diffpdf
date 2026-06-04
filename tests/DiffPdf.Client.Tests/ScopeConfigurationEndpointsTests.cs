using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiffPdf.Client.Tests;

/// <summary>
/// End-to-end checks of the per-scope configuration endpoints against the in-memory API: global defaults,
/// branch-custom inheritance into an instance, optimistic-concurrency conflict, and per-level source validation.
/// </summary>
public sealed class ScopeConfigurationEndpointsTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static object TriggerConfig(string pattern) => new { searchPattern = pattern, recursive = true, maxDegreeOfParallelism = 0 };
    private static object Comparer(int dpi) => new { mode = "Both", dpi, strictness = "Balanced" };

    [Fact]
    public async Task Global_HasCustomDefaults()
    {
        var g = await _client.GetFromJsonAsync<JsonElement>("/api/v1/config/global");
        Assert.Equal("Global", g.GetProperty("level").GetString());
        Assert.Equal("Global", g.GetProperty("triggerResolvedFrom").GetString());
        Assert.Equal("Global", g.GetProperty("comparerResolvedFrom").GetString());
    }

    [Fact]
    public async Task BranchCustom_IsInheritedByInstance()
    {
        await _client.PostAsJsonAsync("/api/v1/branches", new { key = "CfgB", name = "CfgB" });
        var inst = await _client.PostAsJsonAsync("/api/v1/branches/CfgB/instances?ensureStructure=false",
            new { key = "CfgI", name = "CfgI", basePath = "/tmp/cfg" });
        Assert.Equal(HttpStatusCode.Created, inst.StatusCode);

        // Read the branch config to learn its version, then make it Custom with a distinctive search pattern.
        var bcfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/CfgB/config");
        long version = bcfg.GetProperty("version").GetInt64();

        var put = await _client.PutAsJsonAsync("/api/v1/branches/CfgB/config", new
        {
            triggerSource = "Custom",
            triggerConfig = TriggerConfig("*.brn"),
            comparerSource = "Global",
            comparisonOptions = Comparer(150),
            version,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // The instance defaults to inheriting the branch → effective trigger comes from the branch.
        var icfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/CfgB/instances/CfgI/config");
        Assert.Equal("Branch", icfg.GetProperty("triggerResolvedFrom").GetString());
        Assert.Equal("*.brn", icfg.GetProperty("effectiveTriggerConfig").GetProperty("searchPattern").GetString());
    }

    [Fact]
    public async Task StaleVersion_Returns409()
    {
        await _client.PostAsJsonAsync("/api/v1/branches", new { key = "CfgConflict", name = "CfgConflict" });

        var put = await _client.PutAsJsonAsync("/api/v1/branches/CfgConflict/config", new
        {
            triggerSource = "Custom",
            triggerConfig = TriggerConfig("*.x"),
            comparerSource = "Global",
            comparisonOptions = Comparer(150),
            version = 999L,
        });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    [Fact]
    public async Task InvalidSourceForLevel_Returns400()
    {
        await _client.PostAsJsonAsync("/api/v1/branches", new { key = "CfgInvalid", name = "CfgInvalid" });
        var bcfg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/CfgInvalid/config");
        long version = bcfg.GetProperty("version").GetInt64();

        // "Branch" is not a valid source at the branch level (only Global or Custom).
        var put = await _client.PutAsJsonAsync("/api/v1/branches/CfgInvalid/config", new
        {
            triggerSource = "Branch",
            triggerConfig = TriggerConfig("*.x"),
            comparerSource = "Global",
            comparisonOptions = Comparer(150),
            version,
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }
}
