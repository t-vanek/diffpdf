using System.Text;
using System.Text.Json;

namespace DiffPdf.Core.Discovery;

/// <summary>
/// Wire format shared by the UDP responder (server) and the discovery client.
/// A client broadcasts <see cref="ProbeMagic"/>; the server replies with
/// <see cref="ResponsePrefix"/> followed by the JSON-encoded descriptor.
/// </summary>
public static class DiscoveryProtocol
{
    public const int DefaultPort = 41234;
    public const string DefaultMulticastAddress = "239.255.41.234";

    /// <summary>Probe payload a client sends to locate servers.</summary>
    public const string ProbeMagic = "DIFFPDF_DISCOVER_v1";

    /// <summary>Prefix on a server's reply, ahead of the JSON descriptor.</summary>
    public const string ResponsePrefix = "DIFFPDF_SERVER_v1 ";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] CreateProbe() => Encoding.UTF8.GetBytes(ProbeMagic);

    public static bool IsProbe(ReadOnlySpan<byte> datagram)
    {
        Span<byte> magic = stackalloc byte[Encoding.UTF8.GetByteCount(ProbeMagic)];
        Encoding.UTF8.GetBytes(ProbeMagic, magic);
        // Tolerate a trailing newline / padding from simple clients.
        return datagram.Length >= magic.Length && datagram[..magic.Length].SequenceEqual(magic);
    }

    public static byte[] CreateResponse(DiffPdfServerDescriptor descriptor)
    {
        string json = JsonSerializer.Serialize(descriptor, JsonOptions);
        return Encoding.UTF8.GetBytes(ResponsePrefix + json);
    }

    /// <summary>Parses a server reply; returns null for anything that is not a valid response.</summary>
    public static DiffPdfServerDescriptor? TryParseResponse(ReadOnlySpan<byte> datagram)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(datagram);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!text.StartsWith(ResponsePrefix, StringComparison.Ordinal))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DiffPdfServerDescriptor>(text[ResponsePrefix.Length..], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
