using System.Net;

namespace DiffPdf.Client;

/// <summary>Thrown when the diffpdf API returns a non-success status; carries the HTTP status and the problem+json detail.</summary>
public sealed class DiffPdfApiException(HttpStatusCode statusCode, string? detail, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;
}
