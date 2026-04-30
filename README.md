# PhotoManager

[![CI](https://github.com/Hawkynt/PhotoManager/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/PhotoManager/actions/workflows/ci.yml)
[![Release](https://github.com/Hawkynt/PhotoManager/actions/workflows/release.yml/badge.svg)](https://github.com/Hawkynt/PhotoManager/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/Hawkynt/PhotoManager?label=release&sort=semver)](https://github.com/Hawkynt/PhotoManager/releases/latest)
[![Latest nightly](https://img.shields.io/github/v/release/Hawkynt/PhotoManager?include_prereleases&label=nightly&sort=date)](https://github.com/Hawkynt/PhotoManager/releases?q=prerelease%3Atrue)
![License](https://img.shields.io/github/license/Hawkynt/PhotoManager)
![Language](https://img.shields.io/github/languages/top/Hawkynt/PhotoManager?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PhotoManager?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/PhotoManager?branch=main)](https://github.com/Hawkynt/PhotoManager/commits/main)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/PhotoManager/total)](https://github.com/Hawkynt/PhotoManager/releases)

> A cross-platform photo manager and lightweight DAM that organises your collection by metadata, develops RAWs/JPEGs non-destructively, and stays out of the way — no local database, no cloud sync, your folders + sidecar XMP are the source of truth.

## Purpose

PhotoManager helps photographers and photo enthusiasts:

- **Organise** sprawling photo libraries by detecting creation dates from EXIF, GPS, filename patterns, and file-system metadata, then sorting into a `yyyy/yyyyMMdd/HHmmss` folder hierarchy.
- **Cull** with Picasa-style picks/rejects (P/X/U hotkeys), star ratings, color labels, perceptual-hash duplicate detection, and side-by-side compare.
- **Tag** with keywords, GPS coordinates (manual map picker, GPX track sync, reverse geocoding, map bookmarks), face detection + clustering, and ONNX-based object detection.
- **Develop** with a Lightroom-lite pipeline: tone (exposure, contrast, highlights, shadows, whites, blacks, clarity, vibrance, saturation), white balance, sharpening + noise reduction, master + R/G/B + parametric curves, HSL, color grading, B&W mixer, split toning, vignette + grain, lens corrections, calibration, crop + perspective, brush/linear/radial local masks with luminance + hue range filters and Add/Subtract/Intersect compositing.
- **Search** the library by keyword, person, place, rating, color label, or any-text — with saved searches, an in-memory index, and instant re-query.
- **Export** geotagged photos to KML for Google Earth or any GPX viewer.

## Features

### What's new (April 2026)
- 🧠 **Smart Patch Selection** in panorama stitchers — per-frame Laplacian-variance sharpness mask with adaptive median threshold; blurry / obstructed regions are excluded so they don't bleed into the panorama. Drives all three stitcher backends (mask-zeroed BGR for OpenCV, weight-scaled feathering for tripod). Toggle via checkbox; ~1s per frame extra.
- 🎬 **Video → frames extractor (ffmpeg)** — drop a video sweep into PhotoManager and ffmpeg splits it into JPEG frames at user-pickable FPS / quality / time-window. Frames pipe straight into the panorama stitcher with one click. Friendly install banner (`winget`/`brew`/`apt`) when ffmpeg isn't on PATH.
- 🌐 **Spherical (360°) stitcher** — third mode in the panorama stitcher: feed N overlapping frames, get a 2:1 equirectangular canvas straight into the 360° viewer (no save round-trip). OpenCvSharp4-backed.
- 🌅 **HDR merge from bracketed exposures** — Greg-Ward MTB alignment + Debevec/Malik response-curve recovery + Reinhard / Drago tone mapping. Pure C#, no native deps. Mismatched-dimension brackets auto-fit-and-pad to the largest frame; manual exposure-time entry when EXIF is missing.
- 🌐 **360° equirectangular viewer** — pan-around viewer for spherical photos with drag yaw/pitch + scroll FOV; auto-detects via 2:1 aspect or `GPano:ProjectionType` metadata. Save the current perspective view as a JPEG.
- 🖼 **Panorama stitcher** — two modes: tripod (cylindrical re-projection + sequential SSD pairwise alignment + linear feathering, no deps) for sweep shots, and hand-held (OpenCvSharp4 high-level `Stitcher` for gigapixel hand-shot panos).
- 🌳 **Hierarchical keyword tree** — `Travel > Italy > Rome` style. Selecting a leaf writes leaf + ancestors as flat `dc:subject` keywords so other tools see standard keywords; the tree itself lives in app-data JSON.
- 📅 **Calendar view** — month-grid of photos by capture date with day-tile thumbnails and drill-into-day pane.
- 🗂 **Burst grouping** — cluster photos taken in rapid succession by `DateTimeOriginal` proximity + filename similarity; tag each group with a `pm:burstId` keyword.
- 🎯 **Geofence auto-tagging** — when a photo's GPS lands inside a saved bookmark's radius, merge the bookmark's place fields. Auto-on-scan toggle + manual "Apply geofences to selection" dialog.
- ☀️🌙 **Sun / moon position calculator** — NOAA solar + Almanac lunar (azimuth + altitude + twilight description) for any photo's time + location. Used by the world-map sun-arrow follow-up.
- 🌤 **Heuristic sky mask** — top-N% rows + blue-dominance + low-saturation + low-edge classifier; appends a brush-mask local adjustment for the detected sky.
- 👁 **Red-eye removal** — flood-fill blob detector inside face bounds (or whole image when face detector unavailable) + luminance-preserving desaturation.
- 💧 **Watermark layer** — text + opacity + position + font-size, applied at render time only (non-destructive). Round-trips via `pm:developSettings`.
- 🪟 **Develop preset on import** — pick a `DevelopTemplateStore` preset in Settings and every newly-imported file with no embedded settings gets it stamped in automatically.
- 🎬 **3D LUT support** — drop `.cube` / `.3dl` files into `%AppData%/PhotoManager/luts/` and they appear as a "Look" picker in the develop window with an opacity slider. Round-trips via `crs:LookName` so 3rd-party tools see the assigned look.
- ✂️ **Click-drag crop overlay** — eight drag-handles + dimmed exterior + rule-of-thirds guides on the develop preview. Auto-shows when the user has a non-default crop, toggleable via a "Show crop handles" button.
- 🧠 **Smart-album rule builder** — composable rules (rating ≥, keyword, person, location, color label, pick state, date range, GPS box) AND/OR-combined; persisted polymorphically in `UserSettingsData`.
- 📊 **Quality flags scan** — Laplacian-variance sharpness + histogram clipping; tags blurry / over- / under-exposed photos with `qa:` keywords for one-click culling.
- 🛤 **GPX track preview on the world map** — load a GPX file, see the route polyline alongside your photo pins.
- 📍 **Nearby-photo radius search** — in the world map, switch to Nearby mode, click anywhere → photos within a log-scale 100m–50km radius are highlighted, the rest dimmed.
- ⚙️ **Settings window + Recent folders + Status-bar progress** — File → Settings consolidates theme, default rename template, recent-folders depth, geocoder/elevation toggles, rate limit. File menu lists last-5 source/output folders. A shared `OperationProgress` strip below the status bar shows long ops with a Cancel button.
- 🎭 **AI subject mask** — MODNet ONNX segmentation auto-creates a brush-mask local adjustment so you can tweak the subject without touching the background.
- 🔀 **Side-by-side compare** — three modes inside the develop window: After only, split view (with grid splitter), or slider-overlay wipe against the un-edited baseline.
- 🎨 **Theme toggle** — Light / Dark / System default, persisted across sessions, theme-aware tile / border brushes.
- 🪂 **Drag-drop** folders onto the source tree (adds them as roots) or files onto the grid (switches to target mode and scans).
- 📤 **KML export** — `Tools → Export KML…` walks the in-memory index and writes one Placemark per geotagged photo.
- 🔁 **Duplicate detection** — 64-bit perceptual hash + Hamming-distance clustering; no DB, in-memory only.
- 🗺️ **Map favourites + reverse-geocode batch** — bookmark "Pizzeria Roma", apply GPS + place names to a selection in one click; resolve city/country across the whole selection.
- ✅❌ **Picks / rejects** — P/X/U hotkeys, 🚩 column, cull-filter chip that AND-combines with star ratings + color labels.
- ✏️ **Batch rename** with metadata tokens (`{date:yyyy-MM-dd}_{city}_{name}`), 🕒 **batch date shift** (camera-clock offset).

### Current Features
- ✅ Cross-platform desktop UI (Avalonia 11 — Windows, macOS, Linux), single-file self-contained executables per platform
- ✅ HDR merge from bracketed exposures (alignment + response curve + tone mapping, hand-rolled in pure C#)
- ✅ Panorama stitching: tripod (cylindrical reprojection) + hand-held gigapixel (OpenCvSharp4) + spherical 360° (equirectangular output, viewer-ready); optional Smart Patch Selection masks out blurry / obstructed regions per frame
- ✅ Video → frames extractor (ffmpeg subprocess; cancellable; pipes into the panorama stitcher)
- ✅ 360° equirectangular spherical-photo viewer with drag-pan + scroll-zoom
- ✅ Lightroom-lite develop pipeline (~all non-AI Adobe parameters), non-destructive via XMP `pm:developSettings` round-trip
- ✅ 3D LUT (.cube / .3dl) creative-look picker with opacity blend and `crs:LookName` round-trip
- ✅ Watermark layer (text + opacity + position + font-size) rendered at output time only
- ✅ Develop preset auto-applied to newly-imported files (via Settings)
- ✅ Click-drag crop overlay with corner + edge handles, dimmed exterior, rule-of-thirds guides
- ✅ AI subject mask (MODNet ONNX) + heuristic sky mask + red-eye removal → brush-dab local adjustments
- ✅ Face detection + clustering (UltraFace + ArcFace ONNX), object detection (YOLOv8 ONNX)
- ✅ GPS map editor, GPX geotagging, reverse geocoding, elevation lookup, triangulation/resection, world map
- ✅ World-map GPX track overlay + Nearby-photo radius search (haversine, log-scale 100m–50km)
- ✅ Map favourites/bookmarks, KML export, batch reverse-geocode, geofence auto-tagging on scan
- ✅ Sun / moon position calculator (NOAA solar + Almanac lunar) with twilight regime
- ✅ Library search (in-memory index, no DB), saved searches, smart-album rule builder (composable clauses)
- ✅ Hierarchical keyword tree flattens to flat `dc:subject` on write
- ✅ Calendar view (photos by capture date in a month grid) and burst-stacks detector
- ✅ Side-by-side compare (After / Split / Slider) inside the develop window
- ✅ Picasa-style picks/rejects (`xmp:Pick`/`xmp:Reject`) with P/X/U hotkeys; quality-flag scan tags blurry / over- / under-exposed photos
- ✅ Perceptual-hash duplicate detection
- ✅ Settings window (theme, default rename template, recent-folders depth, default develop preset, geocoder/elevation toggles, rate limit, geofence-on-scan toggle)
- ✅ Recent folders submenu (last 5 source + 5 output folders, persisted)
- ✅ Status-bar progress strip with Cancel for long ops, bound to a shared `IProgress<T>` sink
- ✅ Theme toggle (Light / Dark / System), persisted in user settings, theme-aware tile / border brushes
- ✅ Drag-drop folders onto source tree / files onto grid
- ✅ Batch rename with metadata-token templates; batch date shift; batch metadata edit
- ✅ Multiple date source detection (EXIF SubIFD, IFD0, GPS, filename, file system) with reliability scoring
- ✅ Folder structure organisation (`yyyy/yyyyMMdd/HHmmss`); duplicate handling with sequential numbering
- ✅ Command-line interface for automation (preview/dry-run, recursive)
- ✅ MVC pattern (UI), atomic metadata writes (preserved mtime), comprehensive unit + integration tests (766 passing)

### Planned Features
- [ ] AI sky mask using a dedicated ONNX model (heuristic version is shipped)
- [ ] Sun/moon arrows on the world map (calculator is shipped; map overlay is the follow-up)
- [ ] Healing brush / spot remover
- [ ] Photometric modelling (photogrammetry / surface normals — requires user-story scoping)
- [ ] Crash-safe metadata write-back queue
- [ ] Background pre-cache of thumbnails

## How It Works

1. **Scanning**: The application scans specified directories for image files
2. **Metadata Extraction**: Extracts dates from multiple sources:
   - GPS timestamp data
   - EXIF metadata (DateTimeOriginal, DateTime)
   - Filename patterns (supports 40+ date formats)
   - File system dates (creation, modification)
3. **Date Selection**: Uses a reliability scoring algorithm to select the most probable original date
4. **Organization**: Moves files into a structured folder hierarchy:
   ```
   InputDirectory/
   ├── 2024/
   │   ├── 20240115/
   │   │   ├── 143022.jpg
   │   │   ├── 143022 (2).jpg
   │   │   └── 145533.png
   ```

## Project Structure

```
PhotoManager/
├── PhotoManager.Core/       # Shared business logic and models
│   ├── Models/             # Data models and DTOs
│   ├── Services/           # Business logic services
│   ├── Interfaces/         # Service contracts
│   └── Enums/              # Shared enumerations
├── PhotoManager.Tests/      # Unit and integration tests
│   ├── Unit/               # Unit tests for individual components
│   └── Integration/        # End-to-end workflow tests
├── PhotoManager.UI/         # Avalonia desktop application (cross-platform)
│   ├── Controllers/        # MVC controllers
│   ├── Views/              # Avalonia AXAML windows and dialogs
│   ├── Models/             # View models
│   └── Resources/          # Localization resources
├── PhotoManager.CLI/        # Command-line interface
├── README.md               # This file
├── TODO.md                 # Development roadmap
└── CLAUDE.md               # AI assistant instructions
```

## Build Instructions

### Prerequisites
- .NET 8.0 SDK or later
- Visual Studio 2022 or VS Code with C# extension

### Building the Solution
```bash
# Clone the repository
git clone <repository-url>
cd PhotoManager

# Restore dependencies
dotnet restore

# Build all projects
dotnet build

# Run tests
dotnet test
```

### Running the Applications

#### GUI Version
```bash
dotnet run --project PhotoManager.UI
```

#### CLI Version
```bash
dotnet run --project PhotoManager.CLI -- --source "C:\Photos\Input"

# With options
dotnet run --project PhotoManager.CLI -- \
  --source "C:\Photos\Input" \
  --recursive \
  --pattern "{Year}/{Date}/{Time}{Extension}" \
  --dry-run
```

## Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura

# Run specific test category
dotnet test --filter Category=Unit
```

## Architecture

The application follows a clean architecture pattern with separation of concerns:

- **Core**: Contains business logic, models, and interfaces. No UI dependencies.
- **UI**: Avalonia 11 desktop application using MVC pattern; runs on Windows, macOS, and Linux from one codebase.
- **CLI**: Command-line interface for automation.
- **Tests**: Comprehensive test coverage using NUnit (currently 573 tests passing).

### No-database principle

PhotoManager has **no local database**. The truth lives in:

1. The folder structure (file location = "imported")
2. The file's own metadata (EXIF, XMP packet)
3. XMP sidecar files (`.xmp` next to each photo) for fields the format doesn't support natively

Caches (face embeddings, perceptual hashes, library index) are in-memory only and rebuilt on scan. Settings live in a small JSON file under `%AppData%/PhotoManager/`.

### Date Detection Priority System

The system assigns reliability scores to different date sources:

1. **GPS Data** (Score: 50) - Most reliable for photos with location data
2. **EXIF SubIFD** (Score: 40) - Original capture date
3. **EXIF IFD0** (Score: 30) - Last modification date
4. **Filename** (Score: 20) - Parsed from filename patterns
5. **File Modified** (Score: 10) - File system modification date
6. **File Created** (Score: 1) - File system creation date

## Configuration

Settings can be configured through:
- UI: Settings dialog
- CLI: Command-line arguments or config file
- Config file: `appsettings.json`

## Known Issues and Limitations

- Image files only — video support is not on the roadmap.
- No cloud storage integration; PhotoManager works entirely on local files. The XMP sidecars and embedded XMP packets are designed to interoperate cleanly with cloud-syncing tools that respect them.
- The AI subject mask requires a one-time ~25 MB MODNet ONNX download (`Tools → Download detection models…` or click `🎭 Detect subject` and confirm the prompt).
- The video → frames extractor requires `ffmpeg` to be installed and on PATH (`winget install ffmpeg` / `brew install ffmpeg` / `apt install ffmpeg`). PhotoManager doesn't bundle the ffmpeg binary because of its size.
- The spherical 360° stitcher uses OpenCV's mosaic-into-canvas fallback (true cleanroom equirectangular reprojection isn't reachable through OpenCvSharp4 4.10's bindings). Output is a 2:1 canvas with the stitched mosaic centred; full-sphere coverage requires aligned input.
- pHash duplicate detection is in-memory only — re-scanning a 10k+ photo library re-computes hashes (mtime-keyed cache short-circuits unchanged files).

## Security Considerations

- The application requires read/write access to specified directories
- No network communication or data collection
- Settings stored locally in user profile
- No sensitive data is logged or transmitted

## Contributing

Contributions are welcome! Please read our contributing guidelines before submitting PRs.

## License

LGPLv3 - See LICENSE file for details

## Support

For issues, feature requests, or questions, please open an issue on GitHub.