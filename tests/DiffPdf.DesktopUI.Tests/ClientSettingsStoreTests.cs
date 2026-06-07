using System.Text.Json;
using DiffPdf.DesktopUI.Configuration;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The connection gear persists settings in the same nested shape as appsettings.json (so the config
/// source binds them back on the next start and drives auto-connect).</summary>
public class ClientSettingsStoreTests
{
    [Fact]
    public void Save_WritesSettings_ThatBindBackToClientConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "diffpdf-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "client-settings.json");
        try
        {
            new ClientSettingsStore(path).Save("http://srv:5275", autoConnect: true, clientId: "cid", clientSecret: "secret");

            Assert.True(File.Exists(path));
            var cfg = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path))!;
            Assert.Equal("http://srv:5275", cfg.Server.Url);
            Assert.True(cfg.Server.AutoConnect);
            Assert.Equal("cid", cfg.Auth.ClientId);
            Assert.Equal("secret", cfg.Auth.ClientSecret);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Save_TrimsUrl_AndNullsBlankCredentials()
    {
        var dir = Path.Combine(Path.GetTempPath(), "diffpdf-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "s.json");
        try
        {
            new ClientSettingsStore(path).Save("  http://x  ", autoConnect: false, clientId: "   ", clientSecret: null);

            var cfg = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path))!;
            Assert.Equal("http://x", cfg.Server.Url);
            Assert.False(cfg.Server.AutoConnect);
            Assert.Null(cfg.Auth.ClientId);
            Assert.Null(cfg.Auth.ClientSecret);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
