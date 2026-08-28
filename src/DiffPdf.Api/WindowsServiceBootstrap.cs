using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace DiffPdf.Api;

/// <summary>
/// Keeps the SCM-facing process alive independently of SQL Server. The supervised API child is started only
/// after SQL is reachable, and is restarted after an unexpected exit. This lets Windows report the service as
/// Running and puts dependency failures in the Application event log instead of failing ServiceMain startup.
/// </summary>
internal static class WindowsServiceBootstrap
{
    private const string ChildMarker = "DIFFPDF_WINDOWS_SERVICE_CHILD";

    public static bool ShouldRun(string[] args) =>
        WindowsServiceHelpers.IsWindowsService() &&
        !string.Equals(Environment.GetEnvironmentVariable(ChildMarker), "1", StringComparison.Ordinal);

    public static async Task RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The DiffPdf Windows Service bootstrap requires Windows.");

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "DiffPdf API");
        builder.Services.Configure<EventLogSettings>(ConfigureEventLog);
        builder.Services.AddSingleton(new ApiChildArguments(args));
        builder.Services.AddHostedService<ApiProcessSupervisor>();
        await builder.Build().RunAsync();
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(EventLogSettings settings) => settings.SourceName = "DiffPdf API";

    private sealed record ApiChildArguments(string[] Values);

    private sealed class ApiProcessSupervisor(
        IConfiguration configuration,
        ApiChildArguments arguments,
        ILogger<ApiProcessSupervisor> logger) : BackgroundService
    {
        private static readonly TimeSpan InitialDatabaseDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxDatabaseDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);
        private Process? child;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Guarantee that StartAsync returns to Windows before the first database operation begins.
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                string? connectionString = configuration.GetConnectionString("SqlServer");
                if (!string.IsNullOrWhiteSpace(connectionString))
                    await WaitAndEnsureDatabaseAsync(connectionString, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    return;

                try
                {
                    child = StartApiChild(arguments.Values);
                    logger.LogInformation("DiffPdf API process started with PID {ProcessId}.", child.Id);
                    await child.WaitForExitAsync(stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                        logger.LogError("DiffPdf API process exited unexpectedly with code {ExitCode}; it will be restarted.", child.ExitCode);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiffPdf API process could not be started; it will be retried.");
                }
                finally
                {
                    child?.Dispose();
                    child = null;
                }

                try { await Task.Delay(RestartDelay, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (child is { HasExited: false })
            {
                try { child.Kill(entireProcessTree: true); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not stop the DiffPdf API child process cleanly."); }
            }
            await base.StopAsync(cancellationToken);
        }

        private Process StartApiChild(IEnumerable<string> originalArguments)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (string argument in originalArguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment[ChildMarker] = "1";
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start did not return a DiffPdf API process.");
        }

        private async Task WaitAndEnsureDatabaseAsync(string connectionString, CancellationToken stoppingToken)
        {
            var delay = InitialDatabaseDelay;
            bool waitingLogged = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EnsureDatabaseAsync(connectionString, stoppingToken);
                    if (waitingLogged)
                        logger.LogInformation("SQL Server is reachable and the DiffPdf database is present; starting the API process.");
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!waitingLogged)
                    {
                        logger.LogWarning(ex, "SQL Server is unavailable. The Windows Service remains Running and will retry in the background.");
                        waitingLogged = true;
                    }
                    try { await Task.Delay(delay, stoppingToken); }
                    catch (OperationCanceledException) { return; }
                    delay = TimeSpan.FromSeconds(Math.Min(MaxDatabaseDelay.TotalSeconds, delay.TotalSeconds * 2));
                }
            }
        }

        private static async Task EnsureDatabaseAsync(string connectionString, CancellationToken cancellationToken)
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
            string database = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(database))
                return;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                if db_id(@db) is null
                begin
                    declare @sql nvarchar(max) = N'create database ' + quotename(@db);
                    exec (@sql);
                end
                """;
            command.Parameters.AddWithValue("@db", database);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
