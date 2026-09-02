# PhotoManager - Development TODO

## Audit (2026-09-02)

Checklist re-verified against the tree. What landed since 2025-09-10:

- **UI moved from WinForms to Avalonia**, so Windows/Linux/macOS run the same front-end.
- **Non-destructive develop pipeline**: `ImageDeveloper`, tone curve, 3D LUTs, film simulation, local adjustments, virtual copies, develop history, compare mode.
- **Restoration + ML stack (ONNX)**: denoise, upscale, colorize (incl. DDColor), inpaint, artifact removal, dehaze, low-light, face restore, depth/bokeh, scratch detection, sky segmentation; NPU/GPU/CPU device selection.
- **Faces & objects**: face detect/embed/cluster, people registry, person timeline, YOLO object detection, tagged regions.
- **Geo**: reverse geocoding, elevation, geofences, KML export, triangulation, photo resection, sun/moon calculations, GPX geotagging, world map, map bookmarks.
- **Compositing**: HDR merge, cylindrical/spherical/tripod panorama stitching, scanned-piece stitching, video frame extraction.
- **Library tooling**: duplicates, burst stacks, smart albums, keyword hierarchy, memories, quality flagging, batch rename, batch date shift, search with saved filters, calendar, slideshow, compare grid.
- **Metadata write path**: XMP sidecars, JPEG/PNG/TIFF/WebP container writers, atomic writes, crash-safe write-back queue.
- **Formats**: HEIC, AVIF, JPEG 2000, HDR/EXR, PSD/PSB, APNG, DDS, PCX, ICO via the `Hawkynt.FileFormats.Images` package.
- **Infrastructure**: assemblies and namespaces renamed to `Hawkynt.PhotoManager.*`, NuGet publishing via Trusted Publishing, CI/nightly/release/generate workflows, benchmark project, `AGENTS.md`, generated `REFERENCE.md`, README screenshots generated in CI.

## Recent Completed Tasks (2025-09-08 - 2025-09-10)
- [x] **Resource Localization**: Replaced all hardcoded strings with resource references for internationalization
- [x] **FileInfo/DirectoryInfo Usage**: Improved code quality by using FileInfo and DirectoryInfo instead of string paths where appropriate
- [x] **UI Performance Optimization**: Fixed scanning performance issue with two-phase file loading
- [x] **About Dialog**: Implemented proper About dialog with MVC pattern
- [x] **Data Binding**: Replaced manual DataGridView row manipulation with proper data binding using strongly-typed models
- [x] **Code Quality**: Addressed TODO comments and improved semantic type usage

## Architecture Restructuring

### Project Structure ✅ COMPLETED
- [x] Create multi-project solution architecture
  - [x] **PhotoManager.Core** - Shared business logic, models, interfaces
  - [x] **PhotoManager.Tests** - NUnit test project
  - [x] **PhotoManager.UI** - Avalonia MVC application
  - [x] **PhotoManager.CLI** - Command-line interface
  - [x] **PhotoManager.Benchmarks** - BenchmarkDotNet suite

### PhotoManager.Core ✅ MOSTLY COMPLETED
- [x] Models
  - [x] Move `FileToImport` to Core
  - [x] Create `PhotoMetadata` model (shipped as `Metadata/FullMetadata`)
  - [x] Create `ImportSettings` model
  - [x] Create `ImportResult` model with statistics
- [x] Services
  - [x] Move and refactor `ImportManager` 
  - [x] Create `IMetadataExtractor` interface and implementation (shipped as `IMetadataReader` / `MetadataReader`)
  - [x] Create `IDateTimeParser` interface and implementation
  - [x] Create `IFileOrganizer` interface and implementation
- [ ] Configuration
  - [ ] Create `IConfiguration` interface (no such type in Core; the UI carries its own `ISettingsService`)
  - [ ] Implement configuration providers

### PhotoManager.Tests ✅ MOSTLY COMPLETED
- [x] Unit Tests
  - [x] DateTimeParser tests (filename parsing) - ✅ All tests passing
  - [x] ImportManager logic tests
  - [x] MetadataExtractor tests
  - [x] FileOrganizer tests
- [ ] Integration Tests
  - [ ] End-to-end import workflow (no test exercises `ImportManager.ProcessDirectoryAsync` — only `MainController` calls it; there is no integration tier, just `Unit/`)
  - [x] File system operations (real temp-directory I/O in the FileOrganizer and ImportManager tests)
- [x] Test Data
  - [x] Sample images with various metadata (synthesised by `Helpers/TestJpegFactory`)
  - [x] Edge cases and error scenarios

### PhotoManager.UI (Avalonia MVC) ✅ COMPLETED
- [x] Models
  - [x] Create ViewModels for data binding
  - [x] Implement INotifyPropertyChanged
  - [x] Use FileInfo/DirectoryInfo instead of string paths
- [x] Views
  - [x] Main window with menu and toolbar
  - [x] Import wizard dialog → Replaced with integrated scan/run workflow
  - [x] Progress dialog with cancellation → Integrated into main form
  - [x] Settings dialog → `SettingsWindow` (theme, library defaults, model folder, geocoder)
  - [x] Preview panel for file organization → Implemented with image preview and metadata
- [x] Controllers
  - [x] MainController
  - [x] AboutController (replaces SettingsController)
  - [x] ImportController → Integrated into MainController
- [x] Infrastructure
  - [x] Implement dependency injection
  - [x] Add logging framework → Using System.Diagnostics
  - [x] Settings persistence → `SettingsService` writes `UserSettingsData` as JSON under AppData
  - [x] Resource localization (multi-language support)

### PhotoManager.CLI ✅ MOSTLY COMPLETED
- [x] Command Structure
  - [x] `import` - Import and organize files (the root command does this; there is no `import` verb)
  - [x] `preview` - Dry run without moving files
  - [x] `metadata` - Read/write metadata and sidecars
  - [x] `faces` - Face detection and clustering
  - [x] `regions` - Region proposals and tagging
  - [x] `models` - List/download ONNX models
  - [ ] `config` - Manage settings
  - [ ] `help` - Display help information (only System.CommandLine's `--help`; no `help` subcommand)
- [x] Arguments
  - [x] `--source` / `-s` - Source directory
  - [x] `--destination` / `-d` - Destination directory (defaults to source)
  - [x] `--recursive` / `-r` - Process subdirectories
  - [x] `--pattern` / `-p` - Custom naming pattern
  - [x] `--dry-run` - Preview without changes
  - [x] `--verbose` / `-v` - Detailed output
  - [x] `--preserve` - Copy instead of move
  - [x] `--duplicates` - Duplicate handling strategy
  - [ ] `--config` / `-c` - Config file path

## Immediate Priority Tasks

### Bug Fixes
- [x] ✅ Fix ParseDateFromFileName_TwoDigitYear_CorrectCentury test (year 50 should parse as 1950) - COMPLETED 2025-09-10
- [x] ✅ Ensure all tests pass consistently - the suite has grown to ~1100 cases across 148 files; CI is the merge gate on Linux/Windows/macOS

### Configuration & Settings
- [ ] Implement IConfiguration interface in Core
- [x] Add settings persistence for UI
- [ ] Create config file support for CLI
- [x] Add user preferences storage

## Features Enhancement

### Core Functionality
- [x] Duplicate detection
  - [x] Hash-based comparison (SHA-256 identity check in `FileOrganizer`)
  - [x] Similar image detection (`PerceptualHash` + `DuplicateFinder` + duplicates window)
  - [x] User-configurable actions (skip/rename/replace) (`DuplicateHandling`: Skip/Rename/Overwrite/Smart)
- [ ] Batch processing improvements
  - [ ] Parallel processing with configurable threads (`ImportSettings.MaxParallelism` exists but no consumer; `ImportManager` still walks files serially)
  - [ ] Resume capability for interrupted operations
  - [ ] Transaction-like rollback on errors
- [ ] Advanced date detection
  - [ ] Machine learning for ambiguous dates
  - [ ] User-defined patterns (the format list is hardcoded in `DateTimeParser`)
  - [ ] Timezone handling

### User Interface Features
- [x] Drag & drop support (source tree and file grid accept dropped files)
- [x] Real-time preview of organization structure (live "Target Location" column)
- [ ] Undo/Redo functionality (develop-history rollback and mask undo only; no global undo/redo stack)
- [x] Batch rename tools (`BatchRenameWindow` + `RenameTokenExpander`, plus batch date shift)
- [x] Filter and search capabilities (search window, saved searches, star/label/pick filters)
- [ ] Thumbnail view with metadata overlay (single preview image plus region/face thumbnails; the library grid is still text rows)
- [ ] Statistics dashboard
- [ ] Export reports (CSV, JSON) (only KML export exists)

### Performance & Reliability
- [x] Implement caching for metadata (`MetadataCache`, `PerceptualHashCache`, `ImageEmbeddingCache`, preview/thumbnail caches)
- [ ] Add progress reporting with ETA (percentage only; `ImportProgress` still carries the ETA TODO)
- [x] Implement cancellation tokens
- [ ] Add retry logic for transient failures (only the metadata `WriteBackQueue` retries with backoff; import/organize does not)
- [ ] Optimize memory usage for large batches
- [ ] Add comprehensive error handling

### Configuration & Customization
- [x] Custom naming patterns with variables (`--pattern`, `RenameTokenExpander`, default rename template in settings)
- [ ] Configurable date source priorities (the reliability table is hardcoded in `ImportManager`)
- [ ] Plugin architecture for extensions
- [ ] Profile management (different settings per use case)
- [ ] Import/Export settings

## Quality Assurance

### Testing
- [x] Achieve 90% code coverage (Currently at 96%) (figure not re-measured since 2025-09)
- [x] ✅ Fix failing two-digit year test - COMPLETED 2025-09-10
- [x] Performance benchmarks (PhotoManager.Benchmarks, BenchmarkDotNet)
- [ ] Memory leak detection
- [x] UI automation tests (Avalonia headless: smoke, layout and screenshot tests)
- [x] Cross-platform testing (Linux/macOS compatibility) (CI matrix: ubuntu, windows, macos)

### Documentation
- [x] API documentation (generated `PhotoManager.Core/REFERENCE.md`)
- [ ] User manual (README covers usage; no standalone manual)
- [x] Developer guide (`AGENTS.md` + README build instructions)
- [x] Architecture documentation (README architecture, project structure and no-database sections)
- [x] Release notes template (`scripts/update-changelog.mjs` buckets commits into the changelog/release notes)

### CI/CD ✅ COMPLETED
- [x] GitHub Actions workflow (ci, nightly, release, generate, shared `_build`)
- [x] Automated testing on PR
- [x] Code coverage reports (coverlet collector; coverage runs in the shared dotnet-ci workflow)
- [x] Release automation (tagged releases plus nightlies with GFS retention)
- [x] NuGet package publishing (for Core library) (`Hawkynt.PhotoManager.Core` via Trusted Publishing)

## Future Enhancements

### Version 2.0
- [x] Video file support (QuickTime/MP4 recognised by the importer; ffmpeg frame extraction in Core and UI)
- [ ] Cloud storage integration (OneDrive, Google Drive)
- [x] Face recognition and tagging (detection, embedding, clustering, people registry, timelines)
- [x] Geo-location mapping (world map, map picker, bookmarks, reverse geocoding, GPX)
- [ ] Social media metadata extraction

### Version 3.0
- [ ] Web interface
- [ ] Mobile app companion
- [x] AI-powered auto-tagging (CLIP encoders + `AutoKeywordTagger` + auto-keyword scan window)
- [ ] Collaborative features
- [ ] Backup and sync capabilities
