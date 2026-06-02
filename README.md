# diffpdf — server-side PDF comparison

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
| `strictness` | `Balanced` | Preset: `Exact` / `Strict` / `Balanced` / `Lenient`. Drives the tolerances below. |
| `pixelTolerance` | *(preset)* | Override per-channel tolerance (0-255); `0` = exact pixel match. |
| `visualThreshold` | *(preset)* | Override min differing-pixel fraction to flag a page; `0` flags a single pixel. |
| `textDifferenceThreshold` | *(preset)* | Override min changed-word fraction to flag text; `0` flags any change. |
| `visualClusterCellSize` | `24` | Highlight cluster size (px); `1` = per-pixel regions. |
| `alignPages` | `true` | Align pages by content (detect insert/delete). |
| `pageMatchThreshold` | `0.2` | Min word overlap to treat two pages as the same page changed (vs add+remove). |
| `detectBlankPages` | `true` | Flag blank/non-blank transitions. |
| `blankPageThreshold` | `0.0002` | Max non-white pixel fraction to count as blank. |
| `detectContentErrors` | `true` | Scan text for error messages. |
| `contentErrorPatterns` | see below | Case-insensitive regexes; defaults include `subreport error`, `#error`. |
| `ignoreRegions` | `[]` | Areas excluded from comparison (see below). |
| `ignoreTextPatterns` | `[]` | Regexes; matching words are dropped before the text diff. |

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
| `produceHighlightedPdf` | `true` | Emit diff PDF for differing files. |
| `highlightLayout` | `SideBySide` | `SideBySide` (old left / new right) or `Single` (changed side only). |
| `renderer` | `Ghostscript` | `Ghostscript` or `Pdfium`. |

The single-pair result (`POST /api/comparisons`) returns `outcome`
(`Compared`/`Failed`), per-document status, a typed per-page breakdown
(`changes`, `differenceScore`, blank flags, regions) and any `contentErrors`.
The batch report aggregates counts (`identical`, `differing`, `errors`,
`filesWithContentErrors`).

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

### Logging

Logging uses **Serilog**, configured in `appsettings.json` (the `Serilog`
section). Out of the box it writes structured logs to the console and to a
daily-rolling file under `logs/` (14 days retained), enriches every event with
the source context and an `Application` property, and logs one summary line per
HTTP request. Adjust sinks/levels in `appsettings.json` — no code change needed.

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
