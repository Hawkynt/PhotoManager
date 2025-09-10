# PhotoManager - Development TODO

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
  - [x] **PhotoManager.UI** - WinForms MVC application
  - [x] **PhotoManager.CLI** - Command-line interface

### PhotoManager.Core ✅ MOSTLY COMPLETED
- [x] Models
  - [x] Move `FileToImport` to Core
  - [x] Create `PhotoMetadata` model
  - [x] Create `ImportSettings` model
  - [x] Create `ImportResult` model with statistics
- [x] Services
  - [x] Move and refactor `ImportManager` 
  - [x] Create `IMetadataExtractor` interface and implementation
  - [x] Create `IDateTimeParser` interface and implementation
  - [x] Create `IFileOrganizer` interface and implementation
- [ ] Configuration
  - [ ] Create `IConfiguration` interface
  - [ ] Implement configuration providers

### PhotoManager.Tests ✅ COMPLETED (96% coverage)
- [x] Unit Tests
  - [x] DateTimeParser tests (filename parsing) - ✅ All tests passing
  - [x] ImportManager logic tests
  - [x] MetadataExtractor tests
  - [x] FileOrganizer tests
- [x] Integration Tests
  - [x] End-to-end import workflow
  - [x] File system operations
- [x] Test Data
  - [x] Sample images with various metadata
  - [x] Edge cases and error scenarios

### PhotoManager.UI (WinForms MVC) ✅ COMPLETED
- [x] Models
  - [x] Create ViewModels for data binding
  - [x] Implement INotifyPropertyChanged
  - [x] Use FileInfo/DirectoryInfo instead of string paths
- [x] Views
  - [x] Main window with menu and toolbar
  - [x] Import wizard dialog → Replaced with integrated scan/run workflow
  - [x] Progress dialog with cancellation → Integrated into main form
  - [x] Settings dialog → Replaced with About dialog and inline settings
  - [x] Preview panel for file organization → Implemented with image preview and metadata
- [x] Controllers
  - [x] MainController
  - [x] AboutController (replaces SettingsController)
  - [x] ImportController → Integrated into MainController
- [x] Infrastructure
  - [x] Implement dependency injection
  - [x] Add logging framework → Using System.Diagnostics
  - [x] Settings persistence → Partially implemented
  - [x] Resource localization (multi-language support)

### PhotoManager.CLI ✅ MOSTLY COMPLETED
- [x] Command Structure
  - [x] `import` - Import and organize files
  - [x] `preview` - Dry run without moving files
  - [ ] `config` - Manage settings
  - [ ] `help` - Display help information (basic help works)
- [x] Arguments
  - [x] `--source` / `-s` - Source directory
  - [x] `--destination` / `-d` - Destination directory (defaults to source)
  - [x] `--recursive` / `-r` - Process subdirectories
  - [x] `--pattern` / `-p` - Custom naming pattern
  - [x] `--dry-run` - Preview without changes
  - [ ] `--verbose` / `-v` - Detailed output
  - [ ] `--config` / `-c` - Config file path

## Immediate Priority Tasks

### Bug Fixes
- [x] ✅ Fix ParseDateFromFileName_TwoDigitYear_CorrectCentury test (year 50 should parse as 1950) - COMPLETED 2025-09-10
- [x] ✅ Ensure all tests pass consistently - All 24 tests passing

### Configuration & Settings
- [ ] Implement IConfiguration interface in Core
- [ ] Add settings persistence for UI
- [ ] Create config file support for CLI
- [ ] Add user preferences storage

## Features Enhancement

### Core Functionality
- [ ] Duplicate detection
  - [ ] Hash-based comparison
  - [ ] Similar image detection
  - [ ] User-configurable actions (skip/rename/replace)
- [ ] Batch processing improvements
  - [ ] Parallel processing with configurable threads
  - [ ] Resume capability for interrupted operations
  - [ ] Transaction-like rollback on errors
- [ ] Advanced date detection
  - [ ] Machine learning for ambiguous dates
  - [ ] User-defined patterns
  - [ ] Timezone handling

### User Interface Features
- [ ] Drag & drop support
- [ ] Real-time preview of organization structure
- [ ] Undo/Redo functionality
- [ ] Batch rename tools
- [ ] Filter and search capabilities
- [ ] Thumbnail view with metadata overlay
- [ ] Statistics dashboard
- [ ] Export reports (CSV, JSON)

### Performance & Reliability
- [ ] Implement caching for metadata
- [ ] Add progress reporting with ETA
- [ ] Implement cancellation tokens
- [ ] Add retry logic for transient failures
- [ ] Optimize memory usage for large batches
- [ ] Add comprehensive error handling

### Configuration & Customization
- [ ] Custom naming patterns with variables
- [ ] Configurable date source priorities
- [ ] Plugin architecture for extensions
- [ ] Profile management (different settings per use case)
- [ ] Import/Export settings

## Quality Assurance

### Testing
- [x] Achieve 90% code coverage (Currently at 96%)
- [x] ✅ Fix failing two-digit year test - COMPLETED 2025-09-10
- [ ] Performance benchmarks
- [ ] Memory leak detection
- [ ] UI automation tests
- [ ] Cross-platform testing (Linux/macOS compatibility)

### Documentation
- [ ] API documentation
- [ ] User manual
- [ ] Developer guide
- [ ] Architecture documentation
- [ ] Release notes template

### CI/CD
- [ ] GitHub Actions workflow
- [ ] Automated testing on PR
- [ ] Code coverage reports
- [ ] Release automation
- [ ] NuGet package publishing (for Core library)

## Future Enhancements

### Version 2.0
- [ ] Video file support
- [ ] Cloud storage integration (OneDrive, Google Drive)
- [ ] Face recognition and tagging
- [ ] Geo-location mapping
- [ ] Social media metadata extraction

### Version 3.0
- [ ] Web interface
- [ ] Mobile app companion
- [ ] AI-powered auto-tagging
- [ ] Collaborative features
- [ ] Backup and sync capabilities