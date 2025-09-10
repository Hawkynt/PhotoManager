# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PhotoManager is a multi-project solution for organizing photo collections by automatically detecting creation dates from various metadata sources and organizing files into a structured folder hierarchy.

## Solution Structure

- **PhotoManager.Core** - Business logic, models, and services
  - Models: FileToImport, ImportSettings, ImportResult, PhotoMetadata
  - Services: ImportManager, DateTimeParser, FileOrganizer, MetadataExtractor
  - Interfaces: IImportManager, IDateTimeParser, IFileOrganizer, ISupportedFormatsService
  - Enums: DateTimeSource, DuplicateHandling, FileOperationResult
- **PhotoManager.Tests** - NUnit unit and integration tests
  - Unit tests: DateTimeParser, ImportManager, MetadataExtractor, FileOrganizer
  - Integration tests: End-to-end import workflow, file system operations
  - Test coverage: ~96% with one known failing test (two-digit year parsing)
- **PhotoManager.UI** - WinForms application with MVC pattern
  - Controllers: MainController, AboutController
  - Views: MainForm, AboutDialog with proper data binding
  - Resources: Localized strings for internationalization
- **PhotoManager.CLI** - Command-line interface for automation
  - Supports: import, preview (dry-run), recursive processing
  - Arguments: --source, --destination, --recursive, --pattern, --dry-run

## Build and Development Commands

```bash
# Build entire solution
dotnet build

# Run tests
dotnet test

# Run CLI version
dotnet run --project PhotoManager.CLI -- --source "C:\Photos"

# Run UI version
dotnet run --project PhotoManager.UI

# Clean and rebuild
dotnet clean
dotnet build

# Run tests with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura

# Run specific test category
dotnet test --filter Category=Unit
dotnet test --filter Category=Integration
```

## Core Architecture

### File Organization Strategy
The application organizes photos into a hierarchical folder structure:
- `{InputDirectory}/yyyy/yyyyMMdd/HHmmss{extension}`
- Duplicate files are handled by appending ` (n)` to the filename

### Date Detection Priority
The system uses multiple date sources with the following reliability hierarchy:
1. **GPS metadata** (highest priority, score: 50)
2. **EXIF SubIFD DateTimeOriginal** (score: 40)
3. **EXIF IFD0 DateTime** (score: 30)
4. **Filename parsing** (score: 20)
5. **File modification date** (score: 10)
6. **File creation date** (lowest priority, score: 1)

Special handling: When both GPS and EXIF dates exist and EXIF is within 1 hour newer than GPS, EXIF is preferred.

### Key Components

**Services/ImportManager.cs**
- Core orchestration service for file import and organization
- Manages date extraction from multiple sources
- Implements logic for selecting the most probable creation date
- Handles filename pattern recognition for date extraction

**Models/FileToImport.cs**
- Encapsulates file metadata extraction using MetadataExtractor library
- Provides async methods for extracting EXIF, GPS, and filesystem dates
- Lazy-loads metadata on first access

**PhotoManager.UI/Program.cs**
- Entry point for WinForms application
- Uses dependency injection container for service registration
- Implements MVC pattern with proper controller initialization

**PhotoManager.CLI/Program.cs**
- Command-line entry point with argument parsing
- Supports both dry-run and actual file organization
- Uses async/await for efficient file processing

## Dependencies

- **MetadataExtractor** (v2.8.1) - For reading EXIF and other metadata from image files
- **Windows Forms** (.NET 8.0) - UI framework for desktop application
- **NUnit** (v4.2.2) - Testing framework
- **Microsoft.Extensions.DependencyInjection** - Dependency injection container
- **System.CommandLine** - Command-line argument parsing (CLI project)

## Important Implementation Details

- Minimum valid date threshold: 1990-01-01 (dates before this are considered bogus)
- Default dates (1970-01-01, 1980-01-01) are filtered out as unreliable
- Extensive filename date pattern matching (40+ formats supported)
- Async enumeration throughout for efficient file processing
- File operations use overwrite protection (`File.Move` with `overwrite:false`)
- Two-digit year interpretation: Years 00-79 → 2000-2079, Years 80-99 → 1980-1999
- Duplicate handling: Sequential numbering with " (n)" suffix
- Supported image formats: jpg, jpeg, png, gif, bmp, tiff, tif, webp, heic, heif, raw, dng, nef, cr2, arw

## Testing Guidelines

- Run all tests before commits: `dotnet test`
- Current test coverage: ~96%
- All tests passing (24 tests total)
- Integration tests use temporary directories under `.agent.tmp`
- Tests are designed to be deterministic and order-independent

## Code Style

- K&R brace style
- Prefer target-typed new over var
- Use primary constructors where applicable
- Prefer readonly/sealed modifiers
- Early exits to reduce nesting
- No unnecessary comments unless explaining complex logic
- Use FileInfo/DirectoryInfo instead of string paths
- Resource strings for all user-facing text (localization support)