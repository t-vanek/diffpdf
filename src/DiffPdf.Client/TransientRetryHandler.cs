using System.Net;

namespace DiffPdf.Client;

/// <summary>
/// Retries <b>idempotent GET</b> requests on transient failures (network errors, request timeouts,
/// HTTP 5xx/408/429) with capped exponential backoff. Non-GET requests are never retried, so writes
/// can't be duplicated. ProblemDetails error bodies are surfaced separately via <see cref="DiffPdfApiException"/>.
/// </summary>
public sealed class TransientRetryHandler(int maxRetries = 3) : DelegatingHandler
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get)
            return await base.SendAsync(request, ct);

        var delay = InitialDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(Clone(request), ct);
                if (attempt >= maxRetries || !IsTransient(response.StatusCode))
                    return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maxRetries) { }
            catch (TaskCanceledException) when (attempt < maxRetries && !ct.IsCancellationRequested) { /* timeout */ }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(MaxDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    // A request message can only be sent once; clone it for each attempt (GET has no body to copy).
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
