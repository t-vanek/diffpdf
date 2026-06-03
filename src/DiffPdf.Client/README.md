# DiffPdf.Client

Typed .NET client SDK for the **diffpdf** PDF-comparison REST API.

Covers the whole flow: create a **branch** + **instance** (which auto-provisions the
`old/`, `new/`, `reports/` folders), inspect the **structure**/PDF content, check
**readiness**, then manage **schedules** and **notification subscriptions** — the
automation that runs the batches. Batches are launched only by a schedule (cron) or
the **run-now** action; jobs are observed (and paused/resumed/cancelled) via the API.

## Install & register

```csharp
using DiffPdf.Client;

// No auth (Auth:Enabled = false on the server):
services.AddDiffPdfClient(new Uri("http://localhost:8080"));

// With M2M (OpenIddict client-credentials):
services.AddDiffPdfClient(new Uri("http://localhost:8080"),
    clientId: "diffpdf-ci", clientSecret: "…", scope: "diffpdf.api");
```

## Run the whole flow

```csharp
public class Demo(DiffPdfClient diff)
{
    public async Task RunAsync()
    {
        await diff.CreateBranchAsync(new("Alfa", "Alfa"));
        await diff.CreateInstanceAsync("Alfa",
            new("LamaEnergy", "Lama Energy", BasePath: "/pdfs/LamaEnergy"));

        // Define the automation: a schedule carries its own cron, options and CI gate.
        await diff.CreateScheduleAsync("Alfa", "LamaEnergy",
            new() { Key = "nightly", Cron = "0 2 * * *" });

        var readiness = await diff.GetReadinessAsync("Alfa", "LamaEnergy");
        if (!readiness.Ready) return; // nothing (or not both sides) to compare

        // Run it now (instead of waiting for the cron) and poll to the report:
        Guid jobId = await diff.RunScheduleNowAsync("Alfa", "LamaEnergy", "nightly");
        var report = await diff.WaitForReportAsync(jobId);
        Console.WriteLine($"{report.Differing} differing of {report.Total} files");
    }
}
```

Manage the automation: `CreateScheduleAsync` / `ListSchedulesAsync` /
`UpdateScheduleAsync` (optimistic concurrency via `Version`) / `DeleteScheduleAsync` /
`RunScheduleNowAsync`, plus `CreateSubscriptionAsync` … for notification subscriptions.
Observe jobs: `GetJobAsync` (poll `Status`/`Progress`) → `PauseJobAsync` /
`ResumeJobAsync` / `CancelJobAsync` / `RetryJobAsync` → `GetReportAsync` /
`GetResultAsync` / `DownloadArtifactAsync`.

Non-success responses throw `DiffPdfApiException` (with the HTTP status and the
`problem+json` detail).
