using System.Text.Json.Serialization;

namespace DiffPdf.Client;

/// <summary>A job as reported by the server (mirrors the REST <c>JobSummary</c>).</summary>
public sealed record DiffPdfJob
{
    public Guid Id { get; init; }
    public string BusinessInstanceKey { get; init; } = string.Empty;
    public string ProjectKey { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double Progress { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>True once the job reached a terminal state.</summary>
    [JsonIgnore]
    public bool IsTerminal => Status is "Completed" or "Failed" or "Cancelled";
}

/// <summary>A business instance / project scope reference.</summary>
public sealed record DiffPdfScope(string BusinessInstanceKey, string ProjectKey);

/// <summary>OAuth token response.</summary>
internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
}

/// <summary>Raised when the server returns a non-success response.</summary>
public sealed class DiffPdfClientException(System.Net.HttpStatusCode statusCode, string message)
    : Exception($"diffpdf request failed ({(int)statusCode} {statusCode}): {message}")
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
