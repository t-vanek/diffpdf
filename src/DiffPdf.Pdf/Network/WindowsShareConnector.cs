using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Pdf.Network;

/// <summary>
/// Connects a UNC share with explicit credentials via the Win32 multiple
/// provider router (WNetAddConnection2) — equivalent to <c>net use</c> without
/// mapping a drive letter. The connection is cancelled on dispose.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShareConnector
{
    private const int ResourceTypeDisk = 0x1;
    private const int ErrorAlreadyAssigned = 85;          // already connected
    private const int ErrorSessionCredentialConflict = 1219;

    public static NetworkShareConnection Connect(string folder, NetworkCredentials credentials, ILogger logger)
    {
        var (server, share, _) = UncPath.Split(folder);
        string shareRoot = $@"\\{server}\{share}";

        var resource = new NetResource
        {
            Scope = 0,
            Type = ResourceTypeDisk,
            DisplayType = 0,
            Usage = 0,
            LocalName = null,
            RemoteName = shareRoot,
            Comment = null,
            Provider = null,
        };

        int result = WNetAddConnection2(ref resource, credentials.Password, credentials.QualifiedUsername, 0);
        if (result is not 0 and not ErrorAlreadyAssigned)
        {
            string hint = result == ErrorSessionCredentialConflict
                ? " (an existing connection to this server uses different credentials)"
                : string.Empty;
            throw new IOException($"Could not connect to {shareRoot} as {credentials.QualifiedUsername}: Win32 error {result}{hint}.");
        }

        logger.LogInformation("Connected to network share {Share} as {User}", shareRoot, credentials.QualifiedUsername);

        return new NetworkShareConnection(folder, () =>
        {
            int cancel = WNetCancelConnection2(shareRoot, 0, true);
            if (cancel != 0)
                logger.LogDebug("WNetCancelConnection2({Share}) returned {Code}", shareRoot, cancel);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
