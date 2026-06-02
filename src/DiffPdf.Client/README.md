# DiffPdf.Client

Klientské SDK pro **vzdálené ovládání diffpdf serveru** z .NET aplikací
(WPF, konzole, služby). Obsahuje:

- **`DiffPdfDiscoveryClient`** — najde server na LAN přes UDP broadcast/multicast.
- **`DiffPdfClient`** — typovaný REST klient (OAuth token handling, scope, dávky,
  polling úloh, report, stažení artefaktů).
- **`DiffPdfLiveProgress`** — živý progress úloh přes SignalR.

Cílí na `net10.0`, závisí na `DiffPdf.Core` (sdílené modely) a
`Microsoft.AspNetCore.SignalR.Client`.

## Najdi server a připoj se

```csharp
using DiffPdf.Client;

// 1) najdi server v síti (nebo si vezmi konkrétní URL)
var discovery = new DiffPdfDiscoveryClient();
var server = await discovery.DiscoverFirstAsync(TimeSpan.FromSeconds(2));
if (server is null) throw new InvalidOperationException("Žádný diffpdf server nenalezen.");

using var client = DiffPdfClient.ForServer(server);

// 2) když má server zapnutou autentizaci, přihlas se (M2M)
var info = await client.GetServerInfoAsync();
if (info.AuthEnabled)
    await client.AuthenticateClientCredentialsAsync("diffpdf-ci", "diffpdf-secret", "diffpdf.api");
```

## Odešli dávku a sleduj ji živě (WPF)

```csharp
using DiffPdf.Core.Models;

// realtime progress (SignalR) — aktualizuj UI přes Dispatcher
await using var live = await client.ConnectLiveProgressAsync();
live.ProgressReceived += p =>
    Application.Current.Dispatcher.Invoke(() =>
        ProgressBar.Value = p.Progress * 100);

// odešli dávku
var job = await client.SubmitBatchAsync(new BatchComparisonRequest
{
    Scope = new JobScope("Alfa", "LamaEnergyAlfa"),
    OldFolder = "share:reports/baseline",      // alias z konfigurace serveru
    NewFolder = "share:reports/build-123",
});
await live.JoinJobAsync(job.Id);

// počkej na dokončení (REST je zdroj pravdy) a stáhni report
var done = await client.WaitForJobAsync(job.Id);
var report = await client.GetReportAsync(job.Id);
Console.WriteLine($"{report.Differing} lišících se z {report.Total}, passed={report.Passed}");
```

REST zůstává zdrojem pravdy — když UI o SignalR event přijde, stav si kdykoli
načteš přes `GetJobAsync` / `WaitForJobAsync`.
