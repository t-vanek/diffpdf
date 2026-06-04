using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiffPdf.Client.Tests;

/// <summary>
/// End-to-end checks of the control-check endpoint against the in-memory API: create → run-now →
/// run history, plus validation. Uses a raw HttpClient (the endpoint is server-side only, not in the SDK).
/// </summary>
public sealed class ControlCheckEndpointsTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_Run_History_RoundTrip()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/checks", new
        {
            key = "health",
            name = "Server health",
            type = "Health",
            scopeKind = "Global",
            intervalSeconds = 60,
            events = new[] { "HealthDegraded" },
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        string id = created.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("Health", created.RootElement.GetProperty("type").GetString());

        // Run now.
        var run = await _client.PostAsync($"/api/v1/checks/{id}/run", content: null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(runDoc.RootElement.GetProperty("outcome").GetString()));

        // History has the run.
        var runs = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/checks/{id}/runs");
        Assert.True(runs.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Create_WithoutCadence_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/checks", new
        {
            key = "nocadence",
            name = "No cadence",
            type = "Readiness",
            scopeKind = "Global",
            // neither cron nor intervalSeconds
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_InstanceScope_RequiresKeys()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/checks", new
        {
            key = "needsinstance",
            name = "Needs instance",
            type = "Readiness",
            scopeKind = "Instance",
            intervalSeconds = 60,
            // missing branchKey / instanceKey
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreatingBranch_AutoProvisionsReadinessCheck()
    {
        string branch = "AP" + Guid.NewGuid().ToString("N")[..8];

        var resp = await _client.PostAsJsonAsync("/api/v1/branches", new { key = branch, name = branch });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        Assert.True(await CheckExistsAsync($"readiness-{branch}"));
    }

    [Fact]
    public async Task CreatingBranch_WithAutoProvisionFalse_SkipsReadinessCheck()
    {
        string branch = "NP" + Guid.NewGuid().ToString("N")[..8];

        var resp = await _client.PostAsJsonAsync($"/api/v1/branches?autoProvision=false", new { key = branch, name = branch });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        Assert.False(await CheckExistsAsync($"readiness-{branch}"));
    }

    private async Task<bool> CheckExistsAsync(string key)
    {
        var checks = await _client.GetFromJsonAsync<JsonElement>("/api/v1/checks");
        return checks.EnumerateArray().Any(c => c.GetProperty("key").GetString() == key);
    }
}
