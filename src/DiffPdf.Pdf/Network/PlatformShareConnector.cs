using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Pdf.Network;

/// <summary>
/// Picks the right OS mechanism to access a folder. Local and already-mounted
/// paths (and any path given without credentials) pass straight through;
/// authenticated UNC shares are connected via Windows WNet or a Linux CIFS mount.
/// </summary>
public sealed class PlatformShareConnector(ILogger<PlatformShareConnector> logger) : INetworkShareConnector
{
    public NetworkShareConnection Connect(string folder, NetworkCredentials? credentials)
    {
        // Nothing to authenticate: rely on existing OS access (local dir, mapped
        // drive, pre-mounted CIFS, or UNC under the service account).
        if (credentials is null || !UncPath.IsUnc(folder))
            return new NetworkShareConnection(folder);

        if (OperatingSystem.IsWindows())
            return WindowsShareConnector.Connect(folder, credentials, logger);

        if (OperatingSystem.IsLinux())
            return LinuxCifsShareConnector.Connect(folder, credentials, logger);

        throw new PlatformNotSupportedException(
            "Authenticated network shares are supported on Windows and Linux only.");
    }
}
