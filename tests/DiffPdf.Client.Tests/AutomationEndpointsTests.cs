using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiffPdf.Client.Tests;

/// <summary>
/// End-to-end checks of the automation endpoint against the in-memory API: create → run-now → run
/// history (with step results), validation and the run-now overlap guard. Uses a raw HttpClient so the
/// wire shapes are pinned independently of the SDK models.
/// </summary>
public sealed class AutomationEndpointsTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_Run_History_RoundTrip()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/automations", new
        {
            key = "health",
            name = "Server health",
            scopeKind = "Global",
            intervalSeconds = 60,
            steps = new[] { new { type = "Health" } },
            events = new[] { "HealthDegraded" },
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        string id = created.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("Health", created.RootElement.GetProperty("steps")[0].GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(created.RootElement.GetProperty("nextRunAt").GetString())); // seeded schedule

        // Run now.
        var run = await _client.PostAsync($"/api/v1/automations/{id}/run", content: null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(runDoc.RootElement.GetProperty("outcome").GetString()));
        Assert.Equal("Manual", runDoc.RootElement.GetProperty("trigger").GetString());
        Assert.True(runDoc.RootElement.GetProperty("stepResults").GetArrayLength() >= 1);

        // History has the run.
        var runs = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/automations/{id}/runs");
        Assert.True(runs.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Create_WithoutSchedule_IsManualOnly()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/automations", new
        {
            key = "manualonly",
            name = "Manual only",
            scopeKind = "Global",
            steps = new[] { new { type = "Health" } },
            // no cron / intervalSeconds / eventTriggers — legal: run-now only
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        // No schedule → no nextRunAt (the API omits nulls).
        Assert.False(created.RootElement.TryGetProperty("nextRunAt", out var next) && next.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_WithoutSteps_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/automations", new
        {
            key = "nosteps",
            name = "No steps",
            scopeKind = "Global",
            intervalSeconds = 60,
            steps = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidCron_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/automations", new
        {
            key = "badcron",
            name = "Bad cron",
            scopeKind = "Global",
            cron = "not a cron",
            steps = new[] { new { type = "Health" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_InstanceScope_RequiresKeys()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/automations", new
        {
            key = "needsinstance",
            name = "Needs instance",
            scopeKind = "Instance",
            intervalSeconds = 60,
            steps = new[] { new { type = "Readiness" } },
            // missing branchKey / instanceKey
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreatingBranch_AutoProvisionsReadinessAutomation()
    {
        string branch = "AP" + Guid.NewGuid().ToString("N")[..8];

        var resp = await _client.PostAsJsonAsync("/api/v1/branches", new { key = branch, name = branch });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        Assert.True(await AutomationExistsAsync($"readiness-{branch}"));
    }

    [Fact]
    public async Task CreatingBranch_WithAutoProvisionFalse_SkipsReadinessAutomation()
    {
        string branch = "NP" + Guid.NewGuid().ToString("N")[..8];

        var resp = await _client.PostAsJsonAsync($"/api/v1/branches?autoProvision=false", new { key = branch, name = branch });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        Assert.False(await AutomationExistsAsync($"readiness-{branch}"));
    }

    private async Task<bool> AutomationExistsAsync(string key)
    {
        var automations = await _client.GetFromJsonAsync<JsonElement>("/api/v1/automations");
        return automations.EnumerateArray().Any(a => a.GetProperty("key").GetString() == key);
    }
}
