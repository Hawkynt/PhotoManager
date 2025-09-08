# PhotoManager.UI

![License](https://img.shields.io/github/license/Hawkynt/PhotoManager)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/%2ENET-8%2E0-purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/PhotoManager?branch=main&path=PhotoManager%2EUI)](https://github.com/Hawkynt/PhotoManager/commits/main/PhotoManager.UI)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/PhotoManager)](https://github.com/Hawkynt/PhotoManager/releases/latest)

> Windows Forms application for organizing and managing photo collections with metadata-driven sorting and duplicate handling.

## Features

- 🖱️ **User-Friendly Interface** - Intuitive Windows Forms GUI with easy navigation
- 📁 **Directory Selection** - Browser dialogs for source and destination folder selection  
- ⚙️ **Configurable Options** - Duplicate handling, recursive processing, preserve originals
- 📊 **Real-Time Progress** - Live progress bar and status updates during processing
- 💾 **Settings Persistence** - Automatically saves and restores user preferences
- ⏹️ **Cancellation Support** - Stop processing operations at any time
- 🎯 **MVC Architecture** - Clean separation of concerns with dependency injection

## Architecture

The application follows the Model-View-Controller pattern:

### Models
- `MainViewModel` - Data binding and property change notifications

### Views  
- `MainForm` - Primary user interface

### Controllers
- `MainController` - Business logic orchestration

## Configuration

Settings are stored in `appsettings.json`:
- Application settings (date patterns, parallelism)
- User preferences (last directories, options)

## Localization

Resources are stored in `.resx` files:
- `Strings.resx` - Default (English) resources
- Add `Strings.de-DE.resx` for German, etc.

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run
```

Or launch from Visual Studio with F5.

## Dependencies

- PhotoManager.Core - Business logic
- Microsoft.Extensions.DependencyInjection - IoC container
- Microsoft.Extensions.Configuration - Settings management

## User Interface

The main window provides:
- Source directory selection
- Optional destination directory
- Start/Cancel processing buttons
- Progress bar with status messages
- Results summary

## Keyboard Shortcuts

- `Ctrl+O` - Select source directory
- `Ctrl+D` - Select destination directory
- `F5` - Start processing
- `Esc` - Cancel operation