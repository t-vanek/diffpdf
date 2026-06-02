# diffpdf — server-side PDF comparison

🌐 **Jazyk / Language:** [English](README.md) · [Čeština](README.cs.md)

A server-side replacement for [diffpdf](https://mark-summerfield.github.io/diffpdf.html),
built in **C# / .NET 10** as a REST API. Designed to compare large numbers of
PDFs in bulk (an `old` folder vs a `new` folder), in addition to single pairs.

**Primary use case — print/report regression testing.** Testers compare a
freshly generated batch of report PDFs (`new`) against a known-good baseline
(`old`) to confirm a new build did not break existing printouts. The engine is
built to surface exactly those regressions: changed content, broken layouts,
blank pages, and error messages baked into the output.

## Features

- **Text comparison** — word-level diff with positional highlights (PdfPig).
- **Pixel-level visual comparison** — per-pixel diff with configurable tolerance
  (down to exact, 0-tolerance matching) and cluster granularity (down to a single
  pixel); Ghostscript by default, PDFium as a fallback.
- **Configurable strictness** — an `Exact` / `Strict` / `Balanced` / `Lenient`
  preset drives the difference-reporting tolerances, each of which can also be
  overridden individually.
- **Page alignment** — inserted/removed pages are detected (Needleman–Wunsch over
  page-text similarity) so they don't cascade into a run of false differences.
- **Typed page classification** — each page is tagged `TextChanged`,
  `VisualChanged`, `PageAdded`, `PageRemoved`, `SizeChanged`, `BecameBlank` or
  `WasBlank`.
- **Blank page detection** — flags pages that became (or stopped being) blank,
  inspecting pixels so it works for scanned pages too.
- **Content-error detection** — scans the rendered text for error messages a
  report engine may have printed into the PDF (e.g. `subreport error`, `#error`);
  patterns are configurable.
- **Ignore dynamic content** — exclude regions (footer date/time, page numbers,
  watermarks) and/or text patterns (timestamps) from both the text and visual
  diff, so legitimately changing content doesn't flag every report.
- **Robust error handling** — corrupt, encrypted, missing or empty PDFs are
  reported as `Error` with a reason instead of crashing the batch.
- **Two-sided highlighted diff PDF** — per differing file, a spread with the
  old page (left, removed content in red) beside the new page (right, added in
  green, visual changes in orange); header strips are labeled with the side and
  page number (`OLD p.3` / `NEW p.4`).
- **Bulk folder comparison** — pairs files by relative path, runs in parallel,
  classifies each pair as `Identical` / `Differs` / `OnlyInOld` / `OnlyInNew` / `Error`.
- **Network share folders** — compare local, mounted, or UNC (`\\server\share`)
  folders; optional per-folder credentials authenticate the share (Windows
  `WNetAddConnection2`, Linux CIFS mount).
- **Async job API** — submit a batch, poll status, download the report and
  artifacts.

## How it works

### A) The comparison engine (core)

Comparing one PDF pair (`old` vs `new`), `ComparisonEngine` runs:

1. **Probe** — both PDFs are opened defensively (page count, sizes, status:
   `Ok` / `Encrypted` / `Unreadable` / `Empty`). An unreadable side yields a
   `Failed` result with a reason — never a crash.
2. **Text extraction** — PdfPig pulls words with their bounding boxes.
3. **Content-error detection** — the text is scanned with configurable regexes
   (`subreport error`, `#error`, …); hits are recorded with side/page/snippet.
4. **Page alignment** — pages are aligned by text similarity (Needleman–Wunsch)
   instead of by index, so an inserted page becomes `PageAdded`, a deleted one
   `PageRemoved`, with no cascade of false diffs.
5. **Per-page-pair comparison**: ignore-filter (drop words in ignore regions /
   matching ignore patterns) → word-level text diff → pixel-level visual diff
   (render + tolerance + shift-tolerance + region clustering) → blank/size
   checks → a **typed page classification** + score.
6. **Highlighted diff PDF** — differing pages become a spread: old (removed in
   red) left, new (added green, visual orange) right, with page-number headers.

All difference regions are stored in **PDF points (bottom-left origin)** so text
and visual results share one coordinate space; the raster writer converts to
pixels at draw time. Bulk comparison just applies this engine to every paired
file and aggregates the results.

### B) The durable job lifecycle

```
Client → POST /api/batch
   │  validate scope (business instance + project), check folders
   ▼
[API]  insert job into PostgreSQL  +  publish RunBatchComparison   ← one transaction (outbox)
   ▼
RabbitMQ  ──►  [Worker / handler]
   │   RunBatchComparison  → TryStart (Queued→Running, optimistic concurrency)
   │   IndexBatch          → pair folders, create file_pair_tasks, set total
   │   CompareFilePair × N → compare one pair, store result, ++processed
   │   FinalizeBatch       → aggregate results into the report, job → Completed
   ▼
PostgreSQL (state) + storage (artifacts)
   ▼
SignalR (live progress)  +  REST polling (source of truth)
```

See **Durable job processing** below for the guarantees behind each step.

## Architecture

```
DiffPdf.Core                Domain models, abstractions, comparison orchestration
                            (WordDiff, TextComparer, PageAligner, ContentErrorDetector,
                            IgnoreFilter, ComparisonEngine, BatchComparer), scope models,
                            storage path provider — no PDF-library dependencies.
DiffPdf.Pdf                 PdfPig text extraction, Ghostscript & PDFium renderers,
                            SkiaSharp pixel diff + blank detector, PdfSharp side-by-side
                            highlight writer, network-share connectors.
DiffPdf.Persistence         Job / business-instance / project store abstractions +
                            in-memory (dev) implementations.
DiffPdf.Persistence.Postgres EF Core (Npgsql) stores with optimistic concurrency,
                            Mapperly entity→domain mapping, transactional outbox.
DiffPdf.Messaging           Wolverine handler + RabbitMQ wiring (RunBatchComparison).
DiffPdf.Worker              Worker-side infrastructure (storage, worker identity, options).
DiffPdf.Api                 ASP.NET Core Minimal API (Serilog, OpenAPI, endpoint groups).
```

### Durable job processing (multi-instance)

For running many jobs across multiple worker instances, the async pipeline is
backed by durable infrastructure rather than in-process state:

- **PostgreSQL is the source of truth** for jobs, business instances, projects,
  progress and report metadata. State transitions use optimistic concurrency
  (`version`) and a lease (`locked_by` / `locked_until`); only one worker can
  move a job `Queued → Running`.
- **RabbitMQ is the transport** for the `RunBatchComparison` command — it never
  holds job state.
- **Wolverine** orchestrates publish/consume with a PostgreSQL durable
  inbox/outbox, so the handler is safe under duplicate delivery: a redelivered
  message simply finds the job is no longer `Queued` and skips.
- **Transactional outbox** — submitting a batch inserts the job and enqueues its
  command in a single EF Core transaction (Wolverine `IDbContextOutbox`), so a
  job can never exist without its message or vice versa.
- **Retry classification** — transient failures (IO/network/broker) are retried
  with cooldown then dead-lettered; permanent failures (bad request, missing
  folder, corrupt input) are recorded as `Failed` and acknowledged, not retried.
- The handler runs the comparison with bounded parallelism and writes
  version-checked progress; a completed job can never be overwritten by a late
  progress update.

Persistence uses **EF Core** (Npgsql) with **Mapperly** for source-generated
entity→domain mapping; atomic state transitions use `ExecuteUpdate` guarded by
`status` + `version`.

**Realtime progress (SignalR).** Clients connect to the `/hubs/jobs` hub and
join a `job:{id}`, `project:{bi}:{proj}` or `business-instance:{bi}` group to
receive `jobProgress` events. SignalR is notification-only — it holds no state,
so a client that misses an event recovers via `GET /api/jobs/{id}`. (A Redis
backplane is needed for more than one API instance.)

**Bounded render concurrency.** A process-wide semaphore (`IPdfWorkLimiter`,
`Pdf:MaxConcurrentOperations`, default 4) caps concurrent PDF renders across all
jobs and instances, so parallelism can't exhaust CPU/RAM no matter how many
jobs run at once.

**Per-file-pair tasking.** A submitted batch is indexed into one
`file_pair_tasks` row per file pair (`IndexBatch`), each pair is compared by its
own `CompareFilePair` message, and the run is aggregated into the final report
by `FinalizeBatch` once an atomic processed counter reaches the total. This
isolates failures (one corrupt PDF is recorded as `Error` without sinking the
batch) and gives precise progress. Individual pairs **retry on transient errors**
(up to `Worker:MaxFilePairAttempts`, with messaging cooldown), and a background
`StaleTaskRecoveryService` requeues and re-dispatches tasks abandoned by a
crashed worker (lease expiry), so a batch **resumes** instead of stalling.

If `ConnectionStrings:Postgres` and `ConnectionStrings:RabbitMq` are configured
the full stack is used; otherwise the API falls back to in-memory stores with an
in-process Wolverine transport (single-instance dev mode).

The engine stores all difference regions in **PDF points (bottom-left origin)**
so text and visual results share one coordinate space; the raster highlight
writer converts to pixels at render time.

## REST API

| Method | Route | Purpose |
|---|---|---|
| `GET`  | `/health` | Liveness probe. |
| `POST` | `/api/business-instances` | Create a business instance (`Alfa`, `RNew`, …). |
| `GET`  | `/api/business-instances` | List business instances. |
| `POST` | `/api/business-instances/{key}/projects` | Create a project under an instance. |
| `GET`  | `/api/business-instances/{key}/projects` | List projects. |
| `POST` | `/api/comparisons` | Compare a single pair (synchronous). |
| `POST` | `/api/batch` | Submit a folder comparison job (async, returns `202`). |
| `GET`  | `/api/jobs` | List jobs. |
| `GET`  | `/api/jobs/{id}` | Job status + progress. |
| `GET`  | `/api/jobs/{id}/report` | Aggregate JSON report (`409` until ready). |
| `GET`  | `/api/jobs/{id}/result` | CI gate verdict: `200` if passed, `422` if the gate failed. |
| `GET`  | `/api/jobs/{id}/artifacts/{**path}` | Download a highlighted diff PDF. |

OpenAPI document is served at `/openapi/v1.json`.

### Example — single pair

```bash
curl -X POST http://localhost:8080/api/comparisons \
  -H 'Content-Type: application/json' \
  -d '{
        "oldPath": "/pdfs/old/report.pdf",
        "newPath": "/pdfs/new/report.pdf",
        "options": { "mode": "Both", "dpi": 150 }
      }'
```

### Example — batch folder comparison

```bash
# 0. create the scope once (business instance + project)
curl -X POST http://localhost:8080/api/business-instances -d '{"key":"Alfa","name":"Alfa"}' -H 'Content-Type: application/json'
curl -X POST http://localhost:8080/api/business-instances/Alfa/projects -d '{"key":"LamaEnergyAlfa","name":"Lama Energy Alfa"}' -H 'Content-Type: application/json'

# 1. submit a batch under that scope
curl -X POST http://localhost:8080/api/batch \
  -H 'Content-Type: application/json' \
  -d '{
        "scope": { "businessInstanceKey": "Alfa", "projectKey": "LamaEnergyAlfa" },
        "oldFolder": "/pdfs/old",
        "newFolder": "/pdfs/new",
        "recursive": true,
        "options": { "mode": "Both", "produceHighlightedPdf": true }
      }'
# -> { "id": "...", "status": "Queued", ... }

# 2. poll
curl http://localhost:8080/api/jobs/<id>

# 3. fetch report
curl http://localhost:8080/api/jobs/<id>/report
```

### Comparison options

| Field | Default | Notes |
|---|---|---|
| `mode` | `Both` | `Text`, `Visual`, or `Both`. |
| `pages` | all | `{ "from": 1, "to": 5 }`. |
| `dpi` | `150` | Visual render resolution. |
| `strictness` | `Balanced` | Preset: `Exact` / `Strict` / `Balanced` / `Lenient`. Drives the tolerances below. |
| `pixelTolerance` | *(preset)* | Override per-channel tolerance (0-255); `0` = exact pixel match. |
| `visualThreshold` | *(preset)* | Override min differing-pixel fraction to flag a page; `0` flags a single pixel. |
| `textDifferenceThreshold` | *(preset)* | Override min changed-word fraction to flag text; `0` flags any change. |
| `shiftTolerance` | *(preset)* | Pixel radius for absorbing sub-pixel/anti-aliasing shifts; `0` = strict positional. |
| `visualClusterCellSize` | `24` | Highlight cluster size (px); `1` = per-pixel regions. |
| `alignPages` | `true` | Align pages by content (detect insert/delete). |
| `pageMatchThreshold` | `0.2` | Min word overlap to treat two pages as the same page changed (vs add+remove). |
| `detectBlankPages` | `true` | Flag blank/non-blank transitions. |
| `blankPageThreshold` | `0.0002` | Max non-white pixel fraction to count as blank. |
| `detectContentErrors` | `true` | Scan text for error messages. |
| `contentErrorPatterns` | see below | Case-insensitive regexes; defaults include `subreport error`, `#error`. |
| `ignoreRegions` | `[]` | Areas excluded from comparison (see below). |
| `ignoreTextPatterns` | `[]` | Regexes; matching words are dropped before the text diff. |
| `produceHighlightedPdf` | `true` | Emit diff PDF for differing files. |
| `highlightLayout` | `SideBySide` | `SideBySide` (old left / new right) or `Single` (changed side only). |
| `renderer` | `Ghostscript` | `Ghostscript` or `Pdfium`. |

The single-pair result (`POST /api/comparisons`) returns `outcome`
(`Compared`/`Failed`), per-document status, a typed per-page breakdown
(`changes`, `differenceScore`, blank flags, regions) and any `contentErrors`.
The batch report aggregates counts (`identical`, `differing`, `errors`,
`filesWithContentErrors`) plus `passed` / `gateViolations`.

#### Ignoring dynamic content

A footer timestamp or page number changes on every run and would otherwise flag
every report. Exclude it by area and/or by text pattern:

```jsonc
{
  "oldPath": "/pdfs/old/report.pdf",
  "newPath": "/pdfs/new/report.pdf",
  "options": {
    "ignoreRegions": [
      // bottom 8% of every page; coordinates are top-left origin
      { "area": { "x": 0, "y": 0.92, "width": 1, "height": 0.08 },
        "unit": "Fraction", "label": "footer" }
    ],
    "ignoreTextPatterns": ["\\d{4}-\\d{2}-\\d{2}"]   // ISO dates
  }
}
```

`unit` is `Fraction` (0-1 of the page) or `Points`; `pages` (optional) limits a
region to specific page numbers.

#### CI gate (batch pass/fail)

Add a `gate` to a batch request to turn the run into a pass/fail check. The
`GET /api/jobs/{id}/result` endpoint then returns `200` when the run passes and
`422` when it fails — ideal for `curl --fail` in a pipeline.

```jsonc
{
  "oldFolder": "/pdfs/old",
  "newFolder": "/pdfs/new",
  "gate": {
    "failOnAnyDifference": true,   // or set maxDifferingFiles
    "maxErrors": 0,
    "maxFilesWithContentErrors": 0
  }
}
```

A null limit means "no limit"; the report exposes `passed` and `gateViolations`.

#### Network share folders

`oldFolder` / `newFolder` may be local paths, OS-mounted shares, or UNC paths
(`\\server\share\...` or `//server/share/...`). A share that needs
authentication takes optional credentials per folder:

```jsonc
{
  "oldFolder": "\\\\fileserver\\reports\\baseline",
  "newFolder": "\\\\fileserver\\reports\\build-123",
  "oldFolderCredentials": { "username": "svc_diff", "password": "…", "domain": "CORP" },
  "newFolderCredentials": { "username": "svc_diff", "password": "…", "domain": "CORP" }
}
```

- **Windows** connects the share with `WNetAddConnection2` (like `net use`, no
  drive letter) and disconnects when the run finishes.
- **Linux** mounts the share via CIFS to a temporary mount point and unmounts
  after. This needs `cifs-utils` (bundled in the Docker image) and the
  privilege to mount (run the container with `--privileged` or
  `--cap-add SYS_ADMIN`).
- Paths **without** credentials (local dirs, mapped drives, pre-mounted shares,
  or UNC under the service account) are used as-is. Send credentials only over
  HTTPS; they are never written to logs or comparison reports.

### Business instances, projects & storage layout

Jobs are scoped to a **business instance** (e.g. `Alfa`, `RNew`, `ROld`) and a
**project** under it (e.g. `LamaEnergyAlfa`). These are data created via the API
and stored in PostgreSQL — never hardcoded. Artifacts live under the scope:

```
storage/{businessInstanceKey}/{projectKey}/jobs/{jobId}/artifacts|reports|logs
```

Keys are validated (`[a-zA-Z0-9_.-]`, ≤64 chars, no `..`) so they can never
escape the storage root. The structure above is example data, not application
behavior.

## Running

### Docker (recommended)

```bash
docker compose up --build
# Brings up PostgreSQL + RabbitMQ + the API on http://localhost:8080,
# comparing ./samples/old vs ./samples/new. Data persists in named volumes.
```

The image installs Ghostscript and the native deps SkiaSharp/PDFium need;
compose wires `ConnectionStrings__Postgres` / `__RabbitMq` and `Storage__RootPath`.

### Local

```bash
dotnet run --project src/DiffPdf.Api
```

Requires the Ghostscript binary on `PATH` (or set `GHOSTSCRIPT_PATH`) for the
visual mode. Override the artifact directory with `DIFFPDF_ARTIFACT_ROOT`.

### Tests

```bash
dotnet test
```

### Logging

Logging uses **Serilog**, configured in `appsettings.json` (the `Serilog`
section). Out of the box it writes structured logs to the console and to a
daily-rolling file under `logs/` (14 days retained), enriches every event with
the source context and an `Application` property, and logs one summary line per
HTTP request. Adjust sinks/levels in `appsettings.json` — no code change needed.

The file-log directory is set by `DIFFPDF_LOG_DIR` (default `logs/`); the Docker
image points it at `/data/logs` so logs persist on the mounted volume.

## Licensing note

The default renderer shells out to **Ghostscript (AGPL v3)**. For internal /
server-side use this is fine, but redistributing a closed-source product that
bundles Ghostscript requires a commercial license from Artifex. The **PDFium**
renderer (BSD) is provided as a license-clean alternative — set
`"renderer": "Pdfium"`. Other libraries: PdfPig (Apache 2.0), PdfSharp (MIT),
SkiaSharp (MIT).

## Roadmap / not yet implemented

- Vector highlight overlay on the original PDF (keeps text selectable).
- SSIM-based perceptual scoring; structural region clustering.
- Authentication / multi-tenant artifact isolation.
- Network-share credentials in the per-file-pair path (currently pre-mounted only).
