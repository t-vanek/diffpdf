using System.Text.Json;
using DiffPdf.Client;

namespace DiffPdf.Client.Tests;

/// <summary>
/// The SDK must survive a newer server adding enum members: an unknown status string degrades to the
/// enum's default member instead of throwing and killing the whole response read (compatibility guard).
/// </summary>
public class TolerantEnumDeserializationTests
{
    private sealed record StatusEnvelope(JobStatus Status, JobStatus? Optional = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new TolerantEnumConverterFactory() },
    };

    [Fact]
    public void Known_member_parses_case_insensitively()
    {
        var parsed = JsonSerializer.Deserialize<StatusEnvelope>("""{"status":"running"}""", Json)!;
        Assert.Equal(JobStatus.Running, parsed.Status);
    }

    [Fact]
    public void Unknown_member_degrades_to_default_instead_of_throwing()
    {
        var parsed = JsonSerializer.Deserialize<StatusEnvelope>("""{"status":"Retrying"}""", Json)!;
        Assert.Equal(default, parsed.Status);
    }

    [Fact]
    public void Unknown_member_in_nullable_enum_degrades_to_default()
    {
        var parsed = JsonSerializer.Deserialize<StatusEnvelope>("""{"status":"Queued","optional":"Retrying"}""", Json)!;
        Assert.Equal(default(JobStatus), parsed.Optional);
    }

    [Fact]
    public void Numeric_value_still_reads()
    {
        var parsed = JsonSerializer.Deserialize<StatusEnvelope>("""{"status":2}""", Json)!;
        Assert.Equal(JobStatus.Running, parsed.Status);
    }

    [Fact]
    public void Writes_member_names_like_JsonStringEnumConverter()
    {
        string json = JsonSerializer.Serialize(new StatusEnvelope(JobStatus.Paused), Json);
        Assert.Contains("\"Paused\"", json);
    }
}
