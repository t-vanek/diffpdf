using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiffPdf.DesktopUI.Services;

/// <summary>Persisted side of one file panel: which backend it showed and where it stood.</summary>
public sealed record FilePanelState(string Backend, string Path);

/// <summary>Persisted file-manager layout (both panels), restored on the next start.</summary>
public sealed record FileManagerPanelsState(FilePanelState? Left, FilePanelState? Right);

/// <summary>
/// Persists the user's client settings to a writable per-user file
/// (<c>%APPDATA%/DiffPdf/client-settings.json</c>) in the same nested shape as <c>appsettings.json</c>, so they
/// survive restarts. The file is added as a configuration source at startup (overriding <c>appsettings.json</c>).
/// Writers MERGE into the existing document — the connection gear must not wipe the file-manager state
/// and vice versa — and each owns only its section (<c>Server</c>/<c>Auth</c> vs <c>FileManager</c>).
/// </summary>
public sealed class ClientSettingsStore(string? filePath = null)
{
    /// <summary>Default location, also added as a config source in <c>App.ConfigureServices</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffPdf", "client-settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath ?? DefaultFilePath;

    public void Save(string? serverUrl, bool autoConnect, string? clientId, string? clientSecret) => Merge(doc =>
    {
        doc["Server"] = new JsonObject { ["Url"] = Clean(serverUrl), ["AutoConnect"] = autoConnect };
        doc["Auth"] = new JsonObject { ["ClientId"] = Clean(clientId), ["ClientSecret"] = Clean(clientSecret) };
    });

    /// <summary>Saves the file-manager panel layout (called on every navigation — the write is tiny).</summary>
    public void SaveFileManagerPanels(FileManagerPanelsState state) => Merge(doc =>
        doc["FileManager"] = JsonSerializer.SerializeToNode(state, Options));

    /// <summary>The persisted panel layout, or null when absent/unreadable (a broken file must not block startup).</summary>
    public FileManagerPanelsState? LoadFileManagerPanels()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonNode.Parse(File.ReadAllText(FilePath))?["FileManager"] is { } node
                ? node.Deserialize<FileManagerPanelsState>(Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Read-modify-write of the settings document, tolerating a missing or corrupt file.</summary>
    private void Merge(Action<JsonObject> mutate)
    {
        JsonObject doc;
        try
        {
            doc = (File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            doc = new JsonObject();
        }
        mutate(doc);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, doc.ToJsonString(Options));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
