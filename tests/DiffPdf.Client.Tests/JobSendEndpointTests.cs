using System.Net;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Validation paths of <c>POST /jobs/{id}/send</c> through the typed client. The happy path needs an SMTP
/// server and a finished job with diff artifacts, so it is covered by the planner unit tests + manual testing;
/// here we pin the request validation order (recipients before job lookup) and the 404 for an unknown job.
/// </summary>
public sealed class JobSendEndpointTests : IClassFixture<InMemoryApiFactory>
{
    private readonly DiffPdfClient _client;

    public JobSendEndpointTests(InMemoryApiFactory factory) => _client = new DiffPdfClient(factory.CreateClient());

    [Fact]
    public async Task Send_without_recipients_is_a_400()
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() =>
            _client.SendJobDiffsAsync(Guid.NewGuid(), new SendJobDiffsRequest { Recipients = [" ", ""] }));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Send_for_an_unknown_job_is_a_404()
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() =>
            _client.SendJobDiffsAsync(Guid.NewGuid(), new SendJobDiffsRequest { Recipients = ["tiskar@firma.cz"] }));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}
