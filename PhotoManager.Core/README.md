# PhotoManager.Core

![License](https://img.shields.io/github/license/Hawkynt/PhotoManager)
![Language](https://img.shields.io/github/languages/top/Hawkynt/PhotoManager?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PhotoManager?branch=main&path=PhotoManager%2ECore)](https://github.com/Hawkynt/PhotoManager/commits/main/PhotoManager.Core)
[![NuGet](https://img.shields.io/nuget/v/PhotoManager%2ECore)](https://www.nuget.org/packages/PhotoManager.Core/)
[![Downloads](https://img.shields.io/nuget/dt/PhotoManager%2ECore)](https://www.nuget.org/packages/PhotoManager.Core/)

> Core library for photo organization and management with metadata-driven sorting, duplicate handling, and flexible import strategies.

## Overview

This library provides the core functionality for photo organization, including:
- 🔍 **Metadata extraction** from image files (EXIF, GPS, filesystem)
- 🎯 **Smart date detection** from multiple sources with reliability scoring
- 📁 **File organization** with configurable folder structure patterns
- 🔄 **Duplicate handling** with multiple resolution strategies (Smart/Rename/Skip/Overwrite)
- 📊 **Progress reporting** and detailed operation results

## Installation

```bash
dotnet add package PhotoManager.Core
```

## Key Components

### Models
- `FileToImport` - Represents a file to be processed with metadata extraction capabilities
- `ImportSettings` - Configuration for import operations
- `ImportResult` - Results and statistics from import operations

### Services
- `ImportManager` - Main orchestration service for file imports
- `DateTimeParser` - Parses dates from filenames using various patterns
- `FileOrganizer` - Handles file movement and path generation

### Interfaces
- `IImportManager` - Contract for import operations
- `IDateTimeParser` - Contract for date parsing
- `IFileOrganizer` - Contract for file organization

### Enums
- `DateTimeSource` - Identifies the source of extracted dates

## Dependencies
- MetadataExtractor (2.8.1) - For reading EXIF and other metadata

## Usage Example

```csharp
using PhotoManager.Core.Services;
using PhotoManager.Core.Models;

var importManager = new ImportManager();
var settings = new ImportSettings {
    SourceDirectory = new DirectoryInfo(@"C:\Photos\Input"),
    DestinationDirectory = new DirectoryInfo(@"C:\Photos\Organized"),
    Recursive = true,
    DryRun = false
};

var progress = new Progress<ImportProgress>(p => {
    Console.WriteLine($"Processing: {p.CurrentFileName} ({p.PercentComplete:F1}%)");
});

var result = await importManager.ProcessDirectoryAsync(settings, progress);
Console.WriteLine($"Processed {result.SuccessfullyProcessed} of {result.TotalFiles} files");
```

## Date Detection Algorithm

The library uses a sophisticated algorithm to determine the most probable creation date:

1. Extracts dates from multiple sources
2. Filters out invalid/default dates
3. Applies reliability scoring
4. Returns the most reliable date

### Reliability Scores
- GPS: 50
- EXIF SubIFD: 40
- EXIF IFD0: 30
- Filename: 20
- File Modified: 10
- File Created: 1