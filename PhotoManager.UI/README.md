# PhotoManager.UI

Windows Forms application for photo organization with MVC architecture.

## Features

- **Drag & Drop Support** - Easily add photos by dragging folders
- **Real-time Progress** - Visual feedback during processing
- **Settings Persistence** - Remembers last used directories
- **Multi-language Support** - Localized user interface
- **MVC Architecture** - Clean separation of concerns

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