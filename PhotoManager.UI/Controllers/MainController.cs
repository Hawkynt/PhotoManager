using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Controllers;

public class MainController(IImportManager importManager, MainViewModel viewModel, ISettingsService settingsService) {
  private CancellationTokenSource? _cancellationTokenSource;

  public async Task ProcessDirectoryAsync() {
    if (viewModel.IsProcessing)
      return;

    if (string.IsNullOrWhiteSpace(viewModel.SourceDirectory)) {
      viewModel.StatusMessage = "Please select a source directory";
      return;
    }

    var sourceDir = new DirectoryInfo(viewModel.SourceDirectory);
    if (!sourceDir.Exists) {
      viewModel.StatusMessage = "Source directory does not exist";
      return;
    }

    viewModel.IsProcessing = true;
    viewModel.StatusMessage = "Processing...";
    viewModel.ProgressValue = 0;
    this._cancellationTokenSource = new CancellationTokenSource();

    try {
      var settings = new ImportSettings {
        SourceDirectory = sourceDir,
        DestinationDirectory = string.IsNullOrWhiteSpace(viewModel.DestinationDirectory)
          ? null
          : new DirectoryInfo(viewModel.DestinationDirectory),
        Recursive = viewModel.Recursive,
        DryRun = false,
        PreserveOriginals = viewModel.PreserveOriginals,
        DuplicateHandling = viewModel.DuplicateHandling
      };

      var progress = new Progress<ImportProgress>(p => {
        viewModel.ProgressValue = (int)p.PercentComplete;
        viewModel.StatusMessage = $"Processing: {p.CurrentFileName} ({p.CurrentFile}/{p.TotalFiles})";
      });

      var result = await importManager.ProcessDirectoryAsync(
        settings,
        progress, this._cancellationTokenSource.Token);

      viewModel.LastResult = result;
      viewModel.StatusMessage = $"Completed: {result.SuccessfullyProcessed} processed, " +
                                      $"{result.Failed} failed, {result.Skipped} skipped";
    } catch (OperationCanceledException) {
      viewModel.StatusMessage = "Operation cancelled";
    } catch (Exception ex) {
      viewModel.StatusMessage = $"Error: {ex.Message}";
    } finally {
      viewModel.IsProcessing = false;
      viewModel.ProgressValue = 0;
      this._cancellationTokenSource?.Dispose();
      this._cancellationTokenSource = null;
      
      // Save settings after processing
      await SaveSettingsAsync();
    }
  }

  public void CancelProcessing() {
    this._cancellationTokenSource?.Cancel();
  }

  public async Task LoadSettingsAsync() {
    var settings = await settingsService.LoadAsync();
    
    viewModel.SourceDirectory = settings.LastSourceDirectory;
    viewModel.DestinationDirectory = settings.LastDestinationDirectory;
    viewModel.DuplicateHandling = settings.DuplicateHandling;
    viewModel.Recursive = settings.Recursive;
    viewModel.PreserveOriginals = settings.PreserveOriginals;
  }

  public async Task SaveSettingsAsync() {
    var settings = new UserSettingsData {
      LastSourceDirectory = viewModel.SourceDirectory,
      LastDestinationDirectory = viewModel.DestinationDirectory,
      DuplicateHandling = viewModel.DuplicateHandling,
      Recursive = viewModel.Recursive,
      PreserveOriginals = viewModel.PreserveOriginals
    };
    
    await settingsService.SaveAsync(settings);
  }

  public void SelectSourceDirectory() {
    using var dialog = new FolderBrowserDialog {
      Description = "Select source directory",
      ShowNewFolderButton = false
    };

    if (!string.IsNullOrEmpty(viewModel.SourceDirectory))
      dialog.SelectedPath = viewModel.SourceDirectory;

    if (dialog.ShowDialog() == DialogResult.OK) {
      viewModel.SourceDirectory = dialog.SelectedPath;
      _ = SaveSettingsAsync(); // Fire and forget
    }
  }

  public void SelectDestinationDirectory() {
    using var dialog = new FolderBrowserDialog {
      Description = "Select destination directory (leave empty to organize in-place)",
      ShowNewFolderButton = true
    };

    if (!string.IsNullOrEmpty(viewModel.DestinationDirectory))
      dialog.SelectedPath = viewModel.DestinationDirectory;

    if (dialog.ShowDialog() == DialogResult.OK) {
      viewModel.DestinationDirectory = dialog.SelectedPath;
      _ = SaveSettingsAsync(); // Fire and forget
    }
  }
}
