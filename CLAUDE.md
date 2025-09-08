# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PhotoManager is a multi-project solution for organizing photo collections by automatically detecting creation dates from various metadata sources and organizing files into a structured folder hierarchy.

## Solution Structure

- **PhotoManager.Core** - Business logic, models, and services
- **PhotoManager.Tests** - NUnit unit and integration tests  
- **PhotoManager.UI** - WinForms application with MVC pattern
- **PhotoManager.CLI** - Command-line interface for automation

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
dotnet test /p:CollectCoverage=true
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

**Program.cs**
- Entry point currently hardcoded with specific directories for processing
- Bypasses the WinForms UI (MainForm is commented out)
- Executes synchronous file organization for multiple input directories

## Dependencies

- **MetadataExtractor** (v2.8.1) - For reading EXIF and other metadata from image files
- **Windows Forms** - UI framework (currently unused in main execution)

## Important Implementation Details

- Minimum valid date threshold: 1990-01-01 (dates before this are considered bogus)
- Default dates (1970-01-01, 1980-01-01) are filtered out as unreliable
- Extensive filename date pattern matching (40+ formats supported)
- Async enumeration throughout for efficient file processing
- File operations use overwrite protection (`File.Move` with `overwrite:false`)