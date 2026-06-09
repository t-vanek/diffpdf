using System.Net.Sockets;

namespace DiffPdf.Messaging;

/// <summary>
/// Splits failures into transient (worth retrying — DB/broker/IO blips) and
/// permanent (bad request, missing folder, corrupt input — retrying is pointless).
/// </summary>
public static class ExceptionClassifier
{
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            // Missing input is a permanent error even though it derives from IOException.
            if (e is DirectoryNotFoundException or FileNotFoundException)
                return false;

            if (e is TimeoutException or IOException or SocketException)
                return true;

            // Avoid a hard reference to RabbitMQ.Client: match by type name.
            string name = e.GetType().FullName ?? string.Empty;
            if (name.Contains("RabbitMQ", StringComparison.Ordinal)
                || name.Contains("BrokerUnreachable", StringComparison.Ordinal))
                return true;

            if (e.InnerException is null) break;
        }
        return false;
    }
}
