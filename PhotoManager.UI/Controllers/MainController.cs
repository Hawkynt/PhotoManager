using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;
using System.ComponentModel;

namespace PhotoManager.UI.Controllers;

public class MainController(IImportManager importManager, MainViewModel viewModel, ISettingsService settingsService, IFileOrganizer fileOrganizer, ISupportedFormatsService? supportedFormatsService = null) {
  private CancellationTokenSource? _cancellationTokenSource;
  private readonly ISupportedFormatsService _supportedFormatsService = supportedFormatsService ?? new SupportedFormatsService();

  public async Task ProcessSelectedFilesAsync(IEnumerable<FileItemModel> selectedFiles) {
    if (viewModel.IsProcessing)
      return;

    var filesToProcess = selectedFiles.Where(f => f.FileInfo != null).ToList();
    if (!filesToProcess.Any()) {
      viewModel.StatusMessage = "No files to process";
      return;
    }

    viewModel.IsProcessing = true;
    viewModel.StatusMessage = "Processing selected files...";
    viewModel.ProgressValue = 0;
    this._cancellationTokenSource = new CancellationTokenSource();

    try {
      var destinationDirectory = string.IsNullOrWhiteSpace(viewModel.DestinationDirectory)
        ? null
        : new DirectoryInfo(viewModel.DestinationDirectory);

      var results = new List<ImportFileResult>();
      var totalFiles = filesToProcess.Count;
      var processed = 0;
      var succeeded = 0;
      var failed = 0;
      var skipped = 0;

      foreach (var fileItem in filesToProcess) {
        if (this._cancellationTokenSource.Token.IsCancellationRequested)
          break;

        try {
          var fileToImport = new FileToImport(fileItem.FileInfo!);
          var mostProbableDate = await importManager.GetMostLogicalCreationDateAsync(fileToImport);

          if (!mostProbableDate.HasValue) {
            results.Add(ImportFileResult.FromException(fileItem.FileInfo!, "Could not determine date"));
            failed++;
          } else {
            var settings = new ImportSettings {
              SourceDirectory = fileItem.FileInfo!.Directory!, // Each file's own directory for in-place fallback
              DestinationDirectory = destinationDirectory, // If specified, all files go here; if null, fallback to SourceDirectory
              DryRun = false,
              PreserveOriginals = viewModel.PreserveOriginals,
              DuplicateHandling = viewModel.DuplicateHandling
            };

            var (result, targetPath, message) = await fileOrganizer.ProcessFileAsync(fileToImport, mostProbableDate.Value, settings);
            
            results.Add(new ImportFileResult(
              fileItem.FileInfo!,
              targetPath,
              result is FileOperationResult.Success or FileOperationResult.DuplicateRemoved,
              result == FileOperationResult.Failed ? message : null,
              mostProbableDate
            ));

            switch (result) {
              case FileOperationResult.Success:
              case FileOperationResult.DuplicateRemoved:
                succeeded++;
                break;
              case FileOperationResult.Failed:
                failed++;
                break;
              case FileOperationResult.Skipped:
                skipped++;
                break;
            }
          }
        } catch (Exception ex) {
          results.Add(ImportFileResult.FromException(fileItem.FileInfo!, ex.Message));
          failed++;
        }

        processed++;
        viewModel.ProgressValue = (int)((double)processed / totalFiles * 100);
        viewModel.StatusMessage = $"Processing: {fileItem.FileName} ({processed}/{totalFiles})";
      }

      var finalResult = new ImportResult {
        TotalFiles = totalFiles,
        SuccessfullyProcessed = succeeded,
        Failed = failed,
        Skipped = skipped,
        FileResults = results,
        ElapsedTime = TimeSpan.Zero // We could add timing if needed
      };

      viewModel.LastResult = finalResult;
      viewModel.StatusMessage = $"Completed: {succeeded} processed, {failed} failed, {skipped} skipped";
    } catch (Exception ex) {
      viewModel.StatusMessage = $"Error: {ex.Message}";
    } finally {
      viewModel.IsProcessing = false;
      viewModel.ProgressValue = 0;
      this._cancellationTokenSource?.Dispose();
      this._cancellationTokenSource = null;
    }
  }

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
      await this.SaveSettingsAsync();
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
    viewModel.TreeViewPaths = settings.TreeViewPaths;
  }

  public async Task SaveSettingsAsync() {
    var settings = new UserSettingsData {
      LastSourceDirectory = viewModel.SourceDirectory,
      LastDestinationDirectory = viewModel.DestinationDirectory,
      DuplicateHandling = viewModel.DuplicateHandling,
      Recursive = viewModel.Recursive,
      PreserveOriginals = viewModel.PreserveOriginals,
      TreeViewPaths = viewModel.TreeViewPaths
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
      _ = this.SaveSettingsAsync(); // Fire and forget
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
      _ = this.SaveSettingsAsync(); // Fire and forget
    }
  }

  public async Task<string> GetTargetLocation(string filePath, string outputPath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    
    try {
      var fileInfo = new FileInfo(filePath);
      if (!fileInfo.Exists)
        return "File not found";
        
      var fileToImport = new FileToImport(fileInfo);
      var mostProbableDate = await importManager.GetMostLogicalCreationDateAsync(fileToImport);
      
      if (!mostProbableDate.HasValue)
        return "Could not determine date";
      
      var settings = new ImportSettings {
        SourceDirectory = fileInfo.Directory!,
        DestinationDirectory = string.IsNullOrWhiteSpace(outputPath) ? null : new DirectoryInfo(outputPath),
        DuplicateHandling = viewModel.DuplicateHandling,
        PreserveOriginals = viewModel.PreserveOriginals,
        DryRun = true
      };
      
      var targetPath = await fileOrganizer.GenerateTargetPath(fileToImport, mostProbableDate.Value, settings);
      return this.GetRelativeTargetPath(targetPath.FullName, filePath, outputPath);
    } catch (Exception ex) {
      return $"Error: {ex.Message}";
    }
  }
  
  private string GetRelativeTargetPath(string fullTargetPath, string sourceFilePath, string outputPath) {
    // If target path is an error, return as is
    if (fullTargetPath.StartsWith("Error:") || fullTargetPath.StartsWith("Could not"))
      return fullTargetPath;
    
    // If no output path specified, we're organizing in-place
    if (string.IsNullOrWhiteSpace(outputPath)) {
      // For in-place organization, show path relative to the source file's directory
      var sourceDir = Path.GetDirectoryName(sourceFilePath);
      if (string.IsNullOrEmpty(sourceDir))
        return fullTargetPath;

      try {
        return Path.GetRelativePath(sourceDir, fullTargetPath);
      } catch {
        // If can't calculate relative path, just show the file structure part
        return this.ExtractRelativeStructure(fullTargetPath);
      }
    }
    
    // Output path is specified, show path relative to output directory
    try {
      var relativePath = Path.GetRelativePath(outputPath, fullTargetPath);
      // If the path starts with ".." it means it's outside the output directory
      return relativePath.StartsWith("..") ? this.ExtractRelativeStructure(fullTargetPath) : relativePath;
    } catch {
      // Fallback to showing just the structure
      return this.ExtractRelativeStructure(fullTargetPath);
    }
  }
  
  private string ExtractRelativeStructure(string fullPath) {
    // Extract just the date-based folder structure and filename
    // Pattern is typically: yyyy/yyyyMMdd/HHmmss.ext
    var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    
    // Find the year folder (4 digits)
    for (var i = 0; i < parts.Length - 2; ++i) {
      if (parts[i].Length == 4 && int.TryParse(parts[i], out _)) {
        // Found the year, return from here onwards
        return Path.Combine(parts.Skip(i).ToArray());
      }
    }
    
    // If no year pattern found, return the last 3 parts (or all if less)
    var takeCount = Math.Min(3, parts.Length);
    return Path.Combine(parts.Skip(parts.Length - takeCount).ToArray());
  }
  
  public async Task<(DateTime? Date, Core.Enums.DateTimeSource Source)> GetMostLogicalDateWithSourceAsync(FileToImport fileToImport) {
    return await importManager.GetMostLogicalCreationDateWithSourceAsync(fileToImport);
  }

  public async Task<SortableBindingList<FileItemModel>> ScanSourceFilesAsync(List<(DirectoryInfo Path, bool Recursive)> sourcePaths) {
    ArgumentNullException.ThrowIfNull(sourcePaths);

    if (!sourcePaths.Any()) {
      viewModel.StatusMessage = "Please select source paths";
      return new SortableBindingList<FileItemModel>();
    }

    viewModel.StatusMessage = "Scanning files...";
    
    try {
      var allFiles = new List<string>();
      
      foreach (var (path, recursive) in sourcePaths) {
        if (!path.Exists) continue;
        
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var extension in await this.GetSupportedExtensionsAsync()) {
          var files = Directory.GetFiles(path.FullName, extension, searchOption);
          allFiles.AddRange(files);
        }
      }

      // Create file items for data binding (without calculating target locations yet for speed)
      var fileItemsList = new List<FileItemModel>();
      foreach (var file in allFiles.Distinct()) {
        var fileInfo = new FileInfo(file);
        var sourcePath = this.GetRelativeSourcePath(file, sourcePaths);

        fileItemsList.Add(new FileItemModel {
          FileName = fileInfo.Name,
          TargetLocation = "Calculating...", // Will be calculated asynchronously
          SourcePath = sourcePath,
          FileInfo = fileInfo
        });
      }

      // Use SortableBindingList for sortable DataGridView
      var fileItems = new SortableBindingList<FileItemModel>(fileItemsList);
      
      viewModel.StatusMessage = $"Found {fileItems.Count} files. Calculating target locations...";
      
      // Calculate target locations in background
      _ = Task.Run(async () => await this.CalculateTargetLocationsAsync(fileItems));
      
      return fileItems;
      
    } catch (Exception ex) {
      viewModel.StatusMessage = $"Scan error: {ex.Message}";
      return new SortableBindingList<FileItemModel>();
    }
  }

  public List<(DirectoryInfo Path, bool Recursive)> GetCheckedSourcePaths(IEnumerable<TreeNode> nodes) {
    ArgumentNullException.ThrowIfNull(nodes);
    
    var allPaths = new List<(DirectoryInfo, bool)>();
    
    foreach (TreeNode node in nodes) {
      if (node.Checked && node.Tag is TreeViewNodeData nodeData) {
        allPaths.Add((nodeData.Path, nodeData.Recursive));
        
        // Only add checked child nodes if parent is NOT recursive
        if (nodeData.Recursive)
          continue;

        foreach (TreeNode childNode in node.Nodes) {
          if (!childNode.Checked || childNode.Tag is not TreeViewNodeData childData)
            continue;

          allPaths.Add((childData.Path, false));
        }
      } else if (node is { Checked: false, Tag: TreeViewNodeData parentData }) {
        // If parent is unchecked, still check for individually checked children
        if (parentData.Recursive)
          continue;

        foreach (TreeNode childNode in node.Nodes) {
          if (!childNode.Checked || childNode.Tag is not TreeViewNodeData childData)
            continue;

          allPaths.Add((childData.Path, false));
        }
      }
    }
    
    // Remove duplicates and paths that are covered by recursive parents
    var recursivePaths = allPaths.Where(p => p.Item2).Select(p => p.Item1.FullName).ToList();

    var finalPaths = new List<(DirectoryInfo, bool)>();
    finalPaths.AddRange(
      from path in allPaths
      let isAlreadyCoveredByRecursive = recursivePaths.Any(rp =>
        path.Item1.FullName.StartsWith(rp, StringComparison.OrdinalIgnoreCase) &&
        !path.Item1.FullName.Equals(rp, StringComparison.OrdinalIgnoreCase))
      where !isAlreadyCoveredByRecursive && !finalPaths.Contains(path)
      select path
    );
    
    return finalPaths;
  }

  private async Task<string[]> GetSupportedExtensionsAsync() {
    return await this._supportedFormatsService.GetSupportedExtensionsAsync();
  }

  private string GetRelativeSourcePath(string filePath, List<(DirectoryInfo Path, bool Recursive)> sourcePaths) {
    // Find the source path that contains this file
    var containingPath = sourcePaths
      .Select(sp => sp.Path.FullName)
      .Where(sp => filePath.StartsWith(sp, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(sp => sp.Length)
      .FirstOrDefault();
    
    if (containingPath == null) {
      // Fallback: show just the directory name of the file
      return Path.GetDirectoryName(filePath) ?? string.Empty;
    }
    
    try {
      // Get relative path from the containing source path
      var relativePath = Path.GetRelativePath(containingPath, filePath);
      var directoryPart = Path.GetDirectoryName(relativePath) ?? string.Empty;
      
      // If it's in the root of the source path, show the source folder name
      if (string.IsNullOrEmpty(directoryPart)) {
        return Path.GetFileName(containingPath);
      }
      
      // Show relative path with TreeView root prefix for clarity
      var sourceFolderName = Path.GetFileName(containingPath);
      return $"{sourceFolderName}{Path.DirectorySeparatorChar}{directoryPart}";
    } catch {
      // Fallback: show just the directory name
      return Path.GetDirectoryName(filePath) ?? string.Empty;
    }
  }

  private async Task CalculateTargetLocationsAsync(SortableBindingList<FileItemModel> fileItems) {
    try {
      var cancellationToken = this._cancellationTokenSource?.Token ?? CancellationToken.None;
      var totalFiles = fileItems.Count;
      var processed = 0;
      var progressLock = new object();
      
      // Use parallel processing with controlled concurrency
      var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limit concurrent operations
      
      await Parallel.ForEachAsync(
        fileItems.Select((item, index) => new { Item = item, Index = index }),
        new ParallelOptions {
          CancellationToken = cancellationToken,
          MaxDegreeOfParallelism = Environment.ProcessorCount * 2
        },
        async (itemWithIndex, ct) => {
          await semaphore.WaitAsync(ct);
          try {
            var targetLocation = await this.GetTargetLocation(
              itemWithIndex.Item.FileInfo?.FullName ?? "", 
              viewModel.DestinationDirectory ?? ""
            );
            
            // Update the item (this will notify the UI through data binding)
            itemWithIndex.Item.TargetLocation = targetLocation;
            
            // Update progress with throttling to prevent UI sluggishness
            lock (progressLock) {
              processed++;
              // Adaptive throttling: update every 10 items for small batches, every 50 for large batches
              var updateInterval = totalFiles > 1000 ? 50 : 10;
              if (processed % updateInterval == 0 || processed == totalFiles) {
                var progressPercent = (int)((double)processed / totalFiles * 100);
                // Update status message on UI thread without blocking parallel operations
                Task.Run(() => {
                  viewModel.StatusMessage = $"Calculating targets: {processed}/{totalFiles} ({progressPercent}%)";
                });
              }
            }
          } finally {
            semaphore.Release();
          }
        }
      );
      
      viewModel.StatusMessage = $"Ready. {fileItems.Count} files found.";
      
    } catch (OperationCanceledException) {
      viewModel.StatusMessage = "Target calculation cancelled.";
    } catch (Exception ex) {
      viewModel.StatusMessage = $"Target calculation error: {ex.Message}";
    }
  }
}
