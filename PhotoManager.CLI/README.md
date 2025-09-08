# PhotoManager.CLI

![License](https://img.shields.io/github/license/Hawkynt/PhotoManager)
![Platform](https://img.shields.io/badge/platform-cross--platform-green)
![.NET](https://img.shields.io/badge/%2ENET-8%2E0-purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PhotoManager?branch=main&path=PhotoManager%2ECLI)](https://github.com/Hawkynt/PhotoManager/commits/main/PhotoManager.CLI)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/PhotoManager)](https://github.com/Hawkynt/PhotoManager/releases/latest)

> Command-line interface for organizing and managing photo collections with metadata-driven sorting and duplicate handling.

## Features

- ⚡ **High Performance** - Multi-threaded processing with progress reporting
- 🔄 **Smart Duplicate Handling** - Content-based duplicate detection and resolution
- 📊 **Detailed Reporting** - Comprehensive statistics and operation summaries  
- 🎛️ **Flexible Configuration** - Multiple duplicate handling strategies and options
- 🔍 **Preview Mode** - Dry-run capability to preview operations without changes
- 🖥️ **Cross-Platform** - Runs on Windows, Linux, and macOS

## Installation

### From Release
Download the latest release from [GitHub Releases](https://github.com/Hawkynt/PhotoManager/releases) and extract to your desired location.

### Build from Source
```bash
git clone https://github.com/Hawkynt/PhotoManager.git
cd PhotoManager/PhotoManager.CLI
dotnet build -c Release
```

The executable will be in `bin/Release/net8.0/PhotoManager.CLI.exe`

## Usage

### Basic Usage

```bash
# Organize photos in-place
PhotoManager.CLI --source "C:\Photos\Unsorted"

# Organize to different directory
PhotoManager.CLI --source "C:\Photos\Unsorted" --destination "C:\Photos\Organized"

# Preview without making changes
PhotoManager.CLI --source "C:\Photos\Unsorted" --dry-run

# Copy files instead of moving
PhotoManager.CLI --source "C:\Photos\Unsorted" --preserve
```

### Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--source` | `-s` | Source directory (required) | - |
| `--destination` | `-d` | Destination directory | In-place |
| `--recursive` | `-r` | Process subdirectories | true |
| `--pattern` | `-p` | Date format pattern | yyyy/yyyyMMdd/HHmmss |
| `--dry-run` | - | Preview without changes | false |
| `--verbose` | `-v` | Detailed output | false |
| `--preserve` | - | Copy instead of move | false |
| `--duplicates` | - | Duplicate handling: Smart, Rename, Skip, Overwrite | Smart |

### Commands

#### Preview Command
Preview organization without making changes:
```bash
PhotoManager.CLI preview --source "C:\Photos"
```

## Date Format Patterns

Custom patterns using standard date format strings:
- `yyyy/MM/dd` - Year/Month/Day folders
- `yyyy/yyyyMMdd/HHmmss` - Default hierarchical structure
- `yyyy-MM-dd/HH-mm-ss` - Date with time folders

## Examples

### Organize vacation photos
```bash
PhotoManager.CLI --source "D:\Vacation2024" --pattern "yyyy/yyyy-MM-dd" --verbose
```

### Preview organization structure
```bash
PhotoManager.CLI preview --source "C:\Downloads\Photos" --destination "C:\Pictures"
```

### Copy photos to backup location
```bash
PhotoManager.CLI --source "C:\Photos" --destination "E:\Backup\Photos" --preserve
```

### Handle duplicates intelligently
```bash
# Smart handling (default) - detects identical files and removes duplicates
PhotoManager.CLI --source "C:\Photos" --duplicates Smart

# Skip existing files
PhotoManager.CLI --source "C:\Photos" --duplicates Skip

# Always rename conflicting files
PhotoManager.CLI --source "C:\Photos" --duplicates Rename

# Overwrite existing files
PhotoManager.CLI --source "C:\Photos" --duplicates Overwrite
```

## Exit Codes

- `0` - Success
- `1` - Error occurred
- `2` - Invalid arguments

## Performance

The CLI supports parallel processing based on available CPU cores. Large collections are processed efficiently with progress reporting.