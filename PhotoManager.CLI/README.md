# PhotoManager.CLI

Command-line interface for batch photo organization.

## Installation

```bash
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