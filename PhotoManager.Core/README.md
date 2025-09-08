# PhotoManager.Core

Core library containing the business logic, models, and services for the PhotoManager application suite.

## Overview

This library provides the core functionality for photo organization, including:
- Metadata extraction from image files
- Date detection from multiple sources
- File organization logic
- Configurable import settings

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