# PhotoManager

![License](https://img.shields.io/github/license/Hawkynt/PhotoManager)
![Language](https://img.shields.io/github/languages/top/Hawkynt/PhotoManager?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PhotoManager?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/PhotoManager?branch=main)](https://github.com/Hawkynt/PhotoManager/commits/main)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/PhotoManager)](https://github.com/Hawkynt/PhotoManager/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/PhotoManager/total)](https://github.com/Hawkynt/PhotoManager/releases)

> A powerful photo organization tool that automatically sorts your photo collection based on metadata and intelligent date detection.

## Purpose

PhotoManager helps photographers and photo enthusiasts organize their digital photo collections by automatically detecting creation dates from various metadata sources and organizing files into a logical folder structure.

## Features

### Current Features
- ✅ Multiple date source detection (EXIF, GPS, filename, file system)
- ✅ Intelligent date selection algorithm with reliability scoring
- ✅ Automatic folder structure creation (Year/Date/Time)
- ✅ Duplicate handling with sequential numbering
- ✅ Support for all common image formats via MetadataExtractor
- ✅ WinForms GUI with preview and metadata display
- ✅ Command-line interface for automation
- ✅ Multi-project clean architecture
- ✅ Comprehensive unit and integration tests (96% coverage)
- ✅ Resource localization for internationalization
- ✅ MVC pattern implementation in UI

### Planned Features
- [ ] Drag-and-drop support in GUI
- [ ] Advanced batch processing with parallel execution
- [ ] Duplicate detection using file hashing
- [ ] Custom naming patterns with variables
- [ ] Undo/Redo functionality
- [ ] Configuration profiles and settings persistence
- [ ] Video file support
- [ ] Cloud storage integration

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
├── PhotoManager.UI/         # WinForms application
│   ├── Controllers/        # MVC controllers
│   ├── Views/              # Forms and dialogs
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

- **Core**: Contains business logic, models, and interfaces
- **UI**: WinForms application using MVC pattern
- **CLI**: Command-line interface for automation
- **Tests**: Comprehensive test coverage using NUnit

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

- Currently only processes image files (video support planned)
- GPS coordinates extraction for location mapping not yet implemented
- No cloud storage integration
- Single-threaded processing (parallel processing planned)
- Settings persistence not yet implemented
- Drag-and-drop support not implemented

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