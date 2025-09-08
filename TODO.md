# PhotoManager - Development TODO

## Architecture Restructuring

### Project Structure
- [ ] Create multi-project solution architecture
  - [ ] **PhotoManager.Core** - Shared business logic, models, interfaces
  - [ ] **PhotoManager.Tests** - NUnit test project
  - [ ] **PhotoManager.UI** - WinForms MVC application
  - [ ] **PhotoManager.CLI** - Command-line interface

### PhotoManager.Core
- [ ] Models
  - [ ] Move `FileToImport` to Core
  - [ ] Create `PhotoMetadata` model
  - [ ] Create `ImportSettings` model
  - [ ] Create `ImportResult` model with statistics
- [ ] Services
  - [ ] Move and refactor `ImportManager` 
  - [ ] Create `IMetadataExtractor` interface and implementation
  - [ ] Create `IDateTimeParser` interface and implementation
  - [ ] Create `IFileOrganizer` interface and implementation
- [ ] Configuration
  - [ ] Create `IConfiguration` interface
  - [ ] Implement configuration providers

### PhotoManager.Tests
- [ ] Unit Tests
  - [ ] DateTimeParser tests (filename parsing)
  - [ ] ImportManager logic tests
  - [ ] MetadataExtractor tests
  - [ ] FileOrganizer tests
- [ ] Integration Tests
  - [ ] End-to-end import workflow
  - [ ] File system operations
- [ ] Test Data
  - [ ] Sample images with various metadata
  - [ ] Edge cases and error scenarios

### PhotoManager.UI (WinForms MVC)
- [ ] Models
  - [ ] Create ViewModels for data binding
  - [ ] Implement INotifyPropertyChanged
- [ ] Views
  - [ ] Main window with menu and toolbar
  - [ ] Import wizard dialog
  - [ ] Progress dialog with cancellation
  - [ ] Settings dialog
  - [ ] Preview panel for file organization
- [ ] Controllers
  - [ ] MainController
  - [ ] ImportController
  - [ ] SettingsController
- [ ] Infrastructure
  - [ ] Implement dependency injection
  - [ ] Add logging framework
  - [ ] Settings persistence (user/app settings)
  - [ ] Resource localization (multi-language support)

### PhotoManager.CLI
- [ ] Command Structure
  - [ ] `import` - Import and organize files
  - [ ] `preview` - Dry run without moving files
  - [ ] `config` - Manage settings
  - [ ] `help` - Display help information
- [ ] Arguments
  - [ ] `--source` / `-s` - Source directory
  - [ ] `--destination` / `-d` - Destination directory
  - [ ] `--recursive` / `-r` - Process subdirectories
  - [ ] `--pattern` / `-p` - Custom naming pattern
  - [ ] `--dry-run` - Preview without changes
  - [ ] `--verbose` / `-v` - Detailed output
  - [ ] `--config` / `-c` - Config file path

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
- [ ] Achieve 90% code coverage
- [ ] Performance benchmarks
- [ ] Memory leak detection
- [ ] UI automation tests
- [ ] Cross-platform testing (if applicable)

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