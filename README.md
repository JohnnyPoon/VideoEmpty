# VideoEmpty

Cross-platform desktop app to add reusable, well-aligned caption "templates" to screen recordings — built with C# / .NET 8, Avalonia UI, SkiaSharp, and FFmpeg.

> **Status:** scaffold + working in-process API + Avalonia UI + HTTP server + MCP server + Skia template renderer + FFmpeg export pipeline.

## Why

CapCut has no template grouping, forcing copy/paste and manual alignment of repeated caption groups. VideoEmpty lets you define a caption *once* (composed of shapes, borders, text, animation, sound effects), then click on the video preview to place instances at the playhead — every instance stays consistent.

## Solution layout

```
src/
  VideoEmpty.Core/        # Domain model, IVideoEmptyApi, .veproj JSON, built-in templates
  VideoEmpty.Rendering/   # SkiaSharp template rasterizer + FFmpeg/ffprobe wrappers + exporter
  VideoEmpty.UI/          # Avalonia desktop app (Windows + macOS)
  VideoEmpty.Server/      # ASP.NET Core HTTP wrapper around IVideoEmptyApi
  VideoEmpty.Mcp/         # JSON-RPC stdio MCP server exposing IVideoEmptyApi tools
tests/
  VideoEmpty.Core.Tests/
  VideoEmpty.Rendering.Tests/
```

`IVideoEmptyApi` is the single API surface; UI calls it directly, HTTP and MCP are thin adapters — guaranteeing parity across local, remote, and AI-driven use.

## Built-in templates

- **Step** — white rectangle, black border, black centered text. Two rows: step number + title. Default slide-in/out from left.
- **Comment** — black rectangle, white border, white text aligned to bottom. Two rows. Default slide-in/out from left.

Both can be customized or duplicated as starting points.

## Requirements

- .NET 8 SDK
- FFmpeg + ffprobe — **the app installs these for you on first run** (Windows: `winget install Gyan.FFmpeg`; macOS: `brew install ffmpeg`). You can also install manually and place them on `PATH`, or set `VIDEOEMPTY_FFMPEG` / `VIDEOEMPTY_FFPROBE`.
- Logs are written to `%LOCALAPPDATA%\VideoEmpty\logs\videoempty.log` (Windows) or `~/.local/state/VideoEmpty/logs/videoempty.log` (macOS/Linux). Toolbar buttons: **Install FFmpeg…** and **Open Log**.

## Build & test

```powershell
dotnet build
dotnet test
```

## Run

```powershell
# Desktop UI
dotnet run --project src/VideoEmpty.UI

# HTTP server (ASP.NET Core, default http://localhost:5000)
dotnet run --project src/VideoEmpty.Server

# MCP server (stdio, JSON-RPC framed with Content-Length headers)
dotnet run --project src/VideoEmpty.Mcp
```

## Project file format

`.veproj` — pretty-printed JSON; round-trips through `ProjectJson`. Element types use a `kind` discriminator (`shape` / `text`).

## How placement works

1. Pause / scrub to the desired time.
2. Click a template in the left palette (it becomes "armed").
3. Click on the video preview → text dialog appears (multi-line; lines map to text elements top-to-bottom).
4. The instance is added with its **center** at the click point (or nearest snap point when snap-to-grid is enabled) and the template's default duration.
5. Edit start/duration/position/text in the right panel; export to MP4 when done.

The **Settings** dialog includes placement options for **Snap to grid** and **Snap points per axis** (e.g. `10` splits width/height into 10 steps). When a template is armed, the preview shows a live placement box on the video frame before you click.
