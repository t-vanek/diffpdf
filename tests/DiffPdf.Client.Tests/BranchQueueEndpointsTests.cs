using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiffPdf.Client.Tests;

/// <summary>
/// End-to-end checks of the run-queue endpoints against the in-memory API: queue-state read, the branch
/// pause/resume hold flag, and 404 for an unknown branch. (Actual enqueue/run requires real input folders;
/// the sequencing rules are covered by BranchQueueDispatcherTests.)
/// </summary>
public sealed class BranchQueueEndpointsTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task CreateBranchAsync(string key) =>
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/v1/branches", new { key, name = key })).StatusCode);

    [Fact]
    public async Task GetQueue_FreshBranch_IsIdleAndNotPaused()
    {
        await CreateBranchAsync("QFresh");

        var q = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/QFresh/queue");

        Assert.False(q.GetProperty("queuePaused").GetBoolean());
        Assert.Equal(0, q.GetProperty("running").GetInt32());
        Assert.Equal(0, q.GetProperty("pending").GetInt32());
        Assert.Equal(0, q.GetProperty("instances").GetArrayLength());
    }

    [Fact]
    public async Task BranchPauseResume_TogglesHoldFlag()
    {
        await CreateBranchAsync("QHold");

        var paused = await _client.PostAsJsonAsync("/api/v1/branches/QHold/queue", new { action = "Pause" });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        // POST returns QueueActionOutcome { state, message }; the branch state is nested under "state".
        using (var doc = JsonDocument.Parse(await paused.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("state").GetProperty("queuePaused").GetBoolean());

        // Persisted: a fresh GET still reports the hold.
        var afterPause = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/QHold/queue");
        Assert.True(afterPause.GetProperty("queuePaused").GetBoolean());

        var resumed = await _client.PostAsJsonAsync("/api/v1/branches/QHold/queue", new { action = "Resume" });
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var afterResume = await _client.GetFromJsonAsync<JsonElement>("/api/v1/branches/QHold/queue");
        Assert.False(afterResume.GetProperty("queuePaused").GetBoolean());
    }

    [Fact]
    public async Task UnknownBranch_Returns404()
    {
        var resp = await _client.GetAsync("/api/v1/branches/NoSuchBranch/queue");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
