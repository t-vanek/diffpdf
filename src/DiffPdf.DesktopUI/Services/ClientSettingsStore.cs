using System.IO;
using System.Text.Json;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Persists the user's connection settings to a writable per-user file
/// (<c>%APPDATA%/DiffPdf/client-settings.json</c>) in the same nested shape as <c>appsettings.json</c>, so they
/// survive restarts and drive auto-connect. The file is added as a configuration source at startup (overriding
/// <c>appsettings.json</c>); this writes it when the user saves from the connection gear. Only the keys the user
/// owns are written — <c>Discovery</c> and others stay sourced from <c>appsettings.json</c>.
/// </summary>
public sealed class ClientSettingsStore(string? filePath = null)
{
    /// <summary>Default location, also added as a config source in <c>App.ConfigureServices</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffPdf", "client-settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath ?? DefaultFilePath;

    public void Save(string? serverUrl, bool autoConnect, string? clientId, string? clientSecret)
    {
        var doc = new
        {
            Server = new { Url = Clean(serverUrl), AutoConnect = autoConnect },
            Auth = new { ClientId = Clean(clientId), ClientSecret = Clean(clientSecret) },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(doc, Options));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
