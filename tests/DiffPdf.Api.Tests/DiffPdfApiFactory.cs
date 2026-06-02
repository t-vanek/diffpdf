using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DiffPdf.Api.Tests;

/// <summary>
/// Boots the API in its dev-fallback configuration (in-memory stores + in-process
/// Wolverine, auth off). UDP discovery is disabled so tests don't contend for the
/// fixed port.
/// </summary>
public sealed class DiffPdfApiFactory : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } =
        Path.Combine(Path.GetTempPath(), "diffpdf-it-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Discovery:Enabled", "false");
        builder.UseSetting("Storage:RootPath", StorageRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
