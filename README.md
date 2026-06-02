# diffpdf — server-side PDF comparison

A server-side replacement for [diffpdf](https://mark-summerfield.github.io/diffpdf.html),
built in **C# / .NET 10** as a REST API. Designed to compare large numbers of
PDFs in bulk (an `old` folder vs a `new` folder), in addition to single pairs.

## Features

- **Text comparison** — word-level diff with positional highlights (PdfPig).
- **Visual comparison** — page rendering + pixel/region diff (Ghostscript by
  default, PDFium as a fallback).
- **Highlighted diff PDF** — per differing file, a raster diff PDF with
  added / removed / changed regions colored.
- **Bulk folder comparison** — pairs files by relative path, runs in parallel,
  classifies each pair as `Identical` / `Differs` / `OnlyInOld` / `OnlyInNew`.
- **Async job API** — submit a batch, poll status, download the report and
  artifacts.

## Architecture

```
DiffPdf.Core         Domain models, abstractions, comparison orchestration
                     (WordDiff, TextComparer, VisualComparer, ComparisonEngine,
                      BatchComparer) — no PDF-library dependencies.
DiffPdf.Pdf          Implementations: PdfPig text extraction, Ghostscript &
                     PDFium renderers, SkiaSharp image diff, PdfSharp highlight
                     writer.
DiffPdf.Persistence  Job store (in-memory for the MVP).
DiffPdf.Worker       Channel-backed queue + background service.
DiffPdf.Api          ASP.NET Core Minimal API.
```

The engine stores all difference regions in **PDF points (bottom-left origin)**
so text and visual results share one coordinate space; the raster highlight
writer converts to pixels at render time.

## REST API

| Method | Route | Purpose |
|---|---|---|
| `GET`  | `/health` | Liveness probe. |
| `POST` | `/api/comparisons` | Compare a single pair (synchronous). |
| `POST` | `/api/batch` | Submit a folder comparison job (async, returns `202`). |
| `GET`  | `/api/jobs` | List jobs. |
| `GET`  | `/api/jobs/{id}` | Job status + progress. |
| `GET`  | `/api/jobs/{id}/report` | Aggregate JSON report (`409` until ready). |
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
# 1. submit
curl -X POST http://localhost:8080/api/batch \
  -H 'Content-Type: application/json' \
  -d '{
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
| `pixelTolerance` | `16` | Per-channel tolerance (0-255). |
| `visualThreshold` | `0.0005` | Min differing-pixel fraction to flag a page. |
| `produceHighlightedPdf` | `true` | Emit diff PDF for differing files. |
| `renderer` | `Ghostscript` | `Ghostscript` or `Pdfium`. |

## Running

### Docker (recommended)

```bash
docker compose up --build
# API on http://localhost:8080, compares ./samples/old vs ./samples/new
```

The image installs Ghostscript and the native deps SkiaSharp/PDFium need.

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

## Licensing note

The default renderer shells out to **Ghostscript (AGPL v3)**. For internal /
server-side use this is fine, but redistributing a closed-source product that
bundles Ghostscript requires a commercial license from Artifex. The **PDFium**
renderer (BSD) is provided as a license-clean alternative — set
`"renderer": "Pdfium"`. Other libraries: PdfPig (Apache 2.0), PdfSharp (MIT),
SkiaSharp (MIT).

## Roadmap / not yet implemented

- Vector highlight overlay on the original PDF (keeps text selectable).
- Persistent job store (SQLite/PostgreSQL) + horizontal scaling.
- SSIM-based perceptual scoring; structural region clustering.
- Authentication / multi-tenant artifact isolation.
