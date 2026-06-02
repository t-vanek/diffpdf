# DiffPdf.Client

Typed .NET client SDK for the **diffpdf** PDF-comparison REST API.

Covers the whole flow: create a **branch** + **instance** (which auto-provisions the
`old/`, `new/`, `reports/` folders), inspect the **structure**/PDF content, check
**readiness**, then create and drive a **batch job** through its lifecycle
(create → start → pause/resume/cancel → report).

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

        var readiness = await diff.GetReadinessAsync("Alfa", "LamaEnergy");
        if (!readiness.Ready) return; // nothing (or not both sides) to compare

        // create → start → poll to completion → report, in one call:
        var report = await diff.RunBatchAsync(new JobScope("Alfa", "LamaEnergy"));
        Console.WriteLine($"{report.Differing} differing of {report.Total} files");
    }
}
```

Or drive the lifecycle by hand: `CreateBatchAsync` → `StartJobAsync` →
`GetJobAsync` (poll `Status`/`Progress`) → `PauseJobAsync` / `ResumeJobAsync` /
`CancelJobAsync` → `GetReportAsync` / `GetResultAsync` / `DownloadArtifactAsync`.

Non-success responses throw `DiffPdfApiException` (with the HTTP status and the
`problem+json` detail).
