using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Pdf.Network;

/// <summary>
/// Picks the right OS mechanism to access a folder. Local and already-mounted
/// paths (and any path given without credentials) pass straight through;
/// authenticated UNC shares are connected via Windows WNet or a Linux CIFS mount,
/// using the mount tuning from <see cref="NetworkOptions"/>.
/// </summary>
public sealed class PlatformShareConnector(
    IOptions<NetworkOptions> options,
    ILogger<PlatformShareConnector> logger) : INetworkShareConnector
{
    private readonly NetworkOptions _options = options.Value;

    public NetworkShareConnection Connect(string folder, NetworkCredentials? credentials)
    {
        // Nothing to authenticate: rely on existing OS access (local dir, mapped
        // drive, pre-mounted CIFS, or UNC under the service account).
        if (credentials is null || !UncPath.IsUnc(folder))
            return new NetworkShareConnection(folder);

        if (OperatingSystem.IsWindows())
            return WindowsShareConnector.Connect(folder, credentials, logger);

        if (OperatingSystem.IsLinux())
            return LinuxCifsShareConnector.Connect(folder, credentials, _options, logger);

        throw new PlatformNotSupportedException(
            "Authenticated network shares are supported on Windows and Linux only.");
    }
}
