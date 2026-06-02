using System.Diagnostics;
using System.Runtime.Versioning;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Pdf.Network;

/// <summary>
/// Mounts a CIFS/SMB share with credentials to a temporary mount point and
/// unmounts it on dispose. Requires <c>mount.cifs</c> (cifs-utils) and the
/// privilege to mount (root / CAP_SYS_ADMIN).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxCifsShareConnector
{
    public static NetworkShareConnection Connect(string folder, NetworkCredentials credentials, ILogger logger)
    {
        var (server, share, subPath) = UncPath.Split(folder);
        string source = $"//{server}/{share}";

        string mountPoint = Path.Combine(Path.GetTempPath(), "diffpdf-mnt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mountPoint);

        // Pass credentials via a 0600 file so they never appear in process args.
        string credentialsFile = Path.Combine(Path.GetTempPath(), "diffpdf-cred-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(credentialsFile,
            $"username={credentials.Username}\npassword={credentials.Password}\ndomain={credentials.Domain ?? string.Empty}\n");
        File.SetUnixFileMode(credentialsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        try
        {
            Run("mount", ["-t", "cifs", source, mountPoint, "-o", $"credentials={credentialsFile},ro"], logger);
        }
        catch
        {
            TryDelete(credentialsFile);
            TryRemoveDir(mountPoint);
            throw;
        }
        finally
        {
            TryDelete(credentialsFile); // consumed by mount
        }

        logger.LogInformation("Mounted {Source} at {MountPoint}", source, mountPoint);

        string accessPath = string.IsNullOrEmpty(subPath)
            ? mountPoint
            : Path.Combine(mountPoint, subPath.Replace('/', Path.DirectorySeparatorChar));

        return new NetworkShareConnection(accessPath, () =>
        {
            try { Run("umount", [mountPoint], logger); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to unmount {MountPoint}", mountPoint); }
            TryRemoveDir(mountPoint);
        });
    }

    private static void Run(string fileName, string[] args, ILogger logger)
    {
        var psi = new ProcessStartInfo { FileName = fileName, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new IOException($"'{fileName}' failed with exit code {process.ExitCode}: {stderr.Trim()}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private static void TryRemoveDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path); } catch (IOException) { /* best effort */ }
    }
}
