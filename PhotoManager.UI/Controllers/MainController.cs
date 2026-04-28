using System.Collections.ObjectModel;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Controllers;

public class MainController(
  IImportManager importManager,
  MainViewModel viewModel,
  ISettingsService settingsService,
  IFileOrganizer fileOrganizer,
  IFolderPicker folderPicker,
  ISupportedFormatsService? supportedFormatsService = null
) {
  private CancellationTokenSource? _cancellationTokenSource;
  private readonly ISupportedFormatsService _supportedFormatsService = supportedFormatsService ?? new SupportedFormatsService();

  public MainViewModel ViewModel => viewModel;

  public ObservableCollection<SourceTreeNode> SourceTreeRoots { get; } = new();

  public SourceTreeNode AddSourceTreeRoot(DirectoryInfo path, bool recursive) {
    var node = new SourceTreeNode(path, recursive, isRoot: true);
    this.PopulateRecursiveChildren(node);
    this.AttachNodeListeners(node);
    this.SourceTreeRoots.Add(node);
    this.SyncTreeStateToViewModel();
    _ = this.SaveSettingsAsync();
    return node;
  }

  public void RemoveSourceTreeRoot(SourceTreeNode node) {
    this.DetachNodeListeners(node);
    this.SourceTreeRoots.Remove(node);
    this.SyncTreeStateToViewModel();
    _ = this.SaveSettingsAsync();
  }

  public void ToggleRecursive(SourceTreeNode node) {
    if (!node.IsRoot)
      return;

    foreach (var child in node.Children)
      child.PropertyChanged -= this.OnTreeNodePropertyChanged;

    node.IsRecursive = !node.IsRecursive;

    if (node.IsRecursive) {
      this.PopulateRecursiveChildren(node);
      foreach (var child in node.Children)
        child.PropertyChanged += this.OnTreeNodePropertyChanged;
    } else {
      node.Children.Clear();
    }

    this.SyncTreeStateToViewModel();
    _ = this.SaveSettingsAsync();
  }

  public void RebuildSourceTreeFromSettings() {
    foreach (var existing in this.SourceTreeRoots)
      this.DetachNodeListeners(existing);
    this.SourceTreeRoots.Clear();

    foreach (var pathData in viewModel.TreeViewPaths) {
      var directory = new DirectoryInfo(pathData.Path);
      if (!directory.Exists)
        continue;

      var node = new SourceTreeNode(directory, pathData.Recursive, isRoot: true) {
        IsChecked = pathData.Checked
      };
      this.PopulateRecursiveChildren(node);
      this.AttachNodeListeners(node);
      this.SourceTreeRoots.Add(node);
    }
  }

  private void AttachNodeListeners(SourceTreeNode node) {
    node.PropertyChanged += this.OnTreeNodePropertyChanged;
    foreach (var child in node.Children)
      child.PropertyChanged += this.OnTreeNodePropertyChanged;
  }

  private void DetachNodeListeners(SourceTreeNode node) {
    node.PropertyChanged -= this.OnTreeNodePropertyChanged;
    foreach (var child in node.Children)
      child.PropertyChanged -= this.OnTreeNodePropertyChanged;
  }

  private void OnTreeNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
    if (e.PropertyName is not (nameof(SourceTreeNode.IsChecked) or nameof(SourceTreeNode.IsRecursive)))
      return;

    this.SyncTreeStateToViewModel();
    _ = this.SaveSettingsAsync();
  }

  public void SyncTreeStateToViewModel() {
    var list = new List<TreeViewPathData>();
    foreach (var node in this.SourceTreeRoots) {
      list.Add(new TreeViewPathData {
        Path = node.Path.FullName,
        Recursive = node.IsRecursive,
        Checked = node.IsChecked
      });
    }

    viewModel.TreeViewPaths = list;
  }

  public IReadOnlyList<(DirectoryInfo Path, bool Recursive)> GetCheckedSourcePaths() {
    var collected = new List<(DirectoryInfo Path, bool Recursive)>();

    foreach (var root in this.SourceTreeRoots) {
      if (root.IsChecked) {
        collected.Add((root.Path, root.IsRecursive));
        if (root.IsRecursive)
          continue;
      }

      foreach (var child in root.Children) {
        if (child.IsChecked)
          collected.Add((child.Path, false));
      }
    }

    var recursivePaths = collected
      .Where(p => p.Recursive)
      .Select(p => p.Path.FullName)
      .ToArray();

    return collected
      .Where(p => !recursivePaths.Any(rp =>
        p.Path.FullName.StartsWith(rp, StringComparison.OrdinalIgnoreCase) &&
        !p.Path.FullName.Equals(rp, StringComparison.OrdinalIgnoreCase)))
      .Distinct()
      .ToList();
  }

  private void PopulateRecursiveChildren(SourceTreeNode root) {
    if (!root.IsRecursive)
      return;

    root.Children.Clear();
    try {
      foreach (var directory in root.Path.GetDirectories())
        root.Children.Add(new SourceTreeNode(directory, isRecursive: false, isRoot: false));
    } catch {
      // Access denied or other enumeration failures — ignore quietly
    }
  }

  public async Task AddSourcePathInteractiveAsync() {
    var picked = await folderPicker.PickFolderAsync("Add source directory");
    if (string.IsNullOrEmpty(picked))
      return;

    var dir = new DirectoryInfo(picked);
    if (!dir.Exists)
      return;

    this.AddSourceTreeRoot(dir, recursive: true);
  }

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
              SourceDirectory = fileItem.FileInfo!.Directory!,
              DestinationDirectory = destinationDirectory,
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
        ElapsedTime = TimeSpan.Zero
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
    viewModel.SavedSearches = settings.SavedSearches;
    this.RebuildSourceTreeFromSettings();
  }

  public Task<ObservableCollection<FileItemModel>> ScanCheckedSourcesAsync()
    => this.ScanSourceFilesAsync(this.GetCheckedSourcePaths());

  /// <summary>
  /// Recursive scan of the destination directory. Treats the target as one
  /// big "source" with recursive=true, so the rest of the pipeline reuses
  /// the same indexing / target-path / filter logic.
  /// </summary>
  public Task<ObservableCollection<FileItemModel>> ScanTargetDirectoryAsync() {
    var dest = (viewModel.DestinationDirectory ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(dest) || !Directory.Exists(dest)) {
      viewModel.StatusMessage = "Destination directory not set or missing.";
      return Task.FromResult(new ObservableCollection<FileItemModel>());
    }
    return this.ScanSourceFilesAsync(new[] { (new DirectoryInfo(dest), Recursive: true) });
  }

  public async Task SaveSettingsAsync() {
    var settings = new UserSettingsData {
      LastSourceDirectory = viewModel.SourceDirectory,
      LastDestinationDirectory = viewModel.DestinationDirectory,
      DuplicateHandling = viewModel.DuplicateHandling,
      Recursive = viewModel.Recursive,
      PreserveOriginals = viewModel.PreserveOriginals,
      TreeViewPaths = viewModel.TreeViewPaths,
      SavedSearches = viewModel.SavedSearches
    };

    await settingsService.SaveAsync(settings);
  }

  public async Task SelectSourceDirectoryAsync() {
    var picked = await folderPicker.PickFolderAsync("Select source directory", viewModel.SourceDirectory);
    if (string.IsNullOrEmpty(picked))
      return;

    viewModel.SourceDirectory = picked;
    _ = this.SaveSettingsAsync();
  }

  public async Task SelectDestinationDirectoryAsync() {
    var picked = await folderPicker.PickFolderAsync("Select destination directory (leave empty to organize in-place)", viewModel.DestinationDirectory);
    if (string.IsNullOrEmpty(picked))
      return;

    viewModel.DestinationDirectory = picked;
    _ = this.SaveSettingsAsync();
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
      return GetRelativeTargetPath(targetPath.FullName, filePath, outputPath);
    } catch (MissingMethodException ex) {
      // Surface the actual signature — this usually means a binary-version
      // mismatch between two assemblies (e.g. MetadataExtractor being loaded
      // from Costura vs. the runtimes/ folder). The signature pinpoints
      // which method isn't binding at runtime.
      return $"Error: binary mismatch — {ex.Message}";
    } catch (TypeLoadException ex) {
      return $"Error: type load — {ex.Message}";
    } catch (FileLoadException ex) {
      return $"Error: assembly load — {ex.FileName} ({ex.Message})";
    } catch (Exception ex) {
      return $"Error: {ex.GetType().Name}: {ex.Message}";
    }
  }

  private static string GetRelativeTargetPath(string fullTargetPath, string sourceFilePath, string outputPath) {
    if (fullTargetPath.StartsWith("Error:") || fullTargetPath.StartsWith("Could not"))
      return fullTargetPath;

    if (string.IsNullOrWhiteSpace(outputPath)) {
      var sourceDir = Path.GetDirectoryName(sourceFilePath);
      if (string.IsNullOrEmpty(sourceDir))
        return fullTargetPath;

      try {
        return Path.GetRelativePath(sourceDir, fullTargetPath);
      } catch {
        return ExtractRelativeStructure(fullTargetPath);
      }
    }

    try {
      var relativePath = Path.GetRelativePath(outputPath, fullTargetPath);
      return relativePath.StartsWith("..") ? ExtractRelativeStructure(fullTargetPath) : relativePath;
    } catch {
      return ExtractRelativeStructure(fullTargetPath);
    }
  }

  private static string ExtractRelativeStructure(string fullPath) {
    var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    for (var i = 0; i < parts.Length - 2; ++i) {
      if (parts[i].Length == 4 && int.TryParse(parts[i], out _)) {
        return Path.Combine(parts.Skip(i).ToArray());
      }
    }

    var takeCount = Math.Min(3, parts.Length);
    return Path.Combine(parts.Skip(parts.Length - takeCount).ToArray());
  }

  public Task<(DateTime? Date, DateTimeSource Source)> GetMostLogicalDateWithSourceAsync(FileToImport fileToImport)
    => importManager.GetMostLogicalCreationDateWithSourceAsync(fileToImport);

  public async Task<ObservableCollection<FileItemModel>> ScanSourceFilesAsync(IReadOnlyList<(DirectoryInfo Path, bool Recursive)> sourcePaths) {
    ArgumentNullException.ThrowIfNull(sourcePaths);

    if (!sourcePaths.Any()) {
      viewModel.StatusMessage = "Please select source paths";
      return new ObservableCollection<FileItemModel>();
    }

    viewModel.StatusMessage = "Scanning files...";

    try {
      var extensions = await this.GetSupportedExtensionsAsync();

      // Enumerating huge directory trees can block the UI for seconds on HDDs;
      // push the walk to a background thread so the status message paints and
      // the app stays responsive.
      var allFiles = await Task.Run(() => {
        var collected = new List<string>();
        foreach (var (path, recursive) in sourcePaths) {
          if (!path.Exists) continue;
          var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
          foreach (var extension in extensions) {
            try {
              collected.AddRange(Directory.EnumerateFiles(path.FullName, extension, option));
            } catch {
              // Swallow permission errors etc. so one bad dir doesn't kill the scan.
            }
          }
        }
        return collected;
      });

      var fileItemsList = new List<FileItemModel>();
      foreach (var file in allFiles.Distinct()) {
        var fileInfo = new FileInfo(file);
        var sourcePath = GetRelativeSourcePath(file, sourcePaths);

        var item = new FileItemModel {
          FileName = fileInfo.Name,
          TargetLocation = "Calculating...",
          SourcePath = sourcePath,
          FileInfo = fileInfo,
        };
        // Seed the search index with filename + source path so the search bar
        // is useful the instant the scan finishes — before background XMP
        // reads populate the full index.
        item.SearchIndex = BuildInitialSearchIndex(item);
        fileItemsList.Add(item);
      }

      var fileItems = new ObservableCollection<FileItemModel>(fileItemsList);

      viewModel.StatusMessage = $"Found {fileItems.Count} files. Indexing metadata...";

      _ = Task.Run(async () => await this.CalculateTargetLocationsAsync(fileItems));

      return fileItems;

    } catch (Exception ex) {
      viewModel.StatusMessage = $"Scan error: {ex.Message}";
      return new ObservableCollection<FileItemModel>();
    }
  }

  private static string BuildInitialSearchIndex(FileItemModel item) {
    var path = item.FileInfo?.FullName ?? string.Empty;
    return (item.FileName + " " + item.SourcePath + " " + path).ToLowerInvariant();
  }

  private async Task<string[]> GetSupportedExtensionsAsync()
    => await this._supportedFormatsService.GetSupportedExtensionsAsync();

  private static string GetRelativeSourcePath(string filePath, IReadOnlyList<(DirectoryInfo Path, bool Recursive)> sourcePaths) {
    var containingPath = sourcePaths
      .Select(sp => sp.Path.FullName)
      .Where(sp => filePath.StartsWith(sp, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(sp => sp.Length)
      .FirstOrDefault();

    if (containingPath == null)
      return Path.GetDirectoryName(filePath) ?? string.Empty;

    try {
      var relativePath = Path.GetRelativePath(containingPath, filePath);
      var directoryPart = Path.GetDirectoryName(relativePath) ?? string.Empty;

      if (string.IsNullOrEmpty(directoryPart))
        return Path.GetFileName(containingPath);

      var sourceFolderName = Path.GetFileName(containingPath);
      return $"{sourceFolderName}{Path.DirectorySeparatorChar}{directoryPart}";
    } catch {
      return Path.GetDirectoryName(filePath) ?? string.Empty;
    }
  }

  private async Task CalculateTargetLocationsAsync(ObservableCollection<FileItemModel> fileItems) {
    try {
      var cancellationToken = this._cancellationTokenSource?.Token ?? CancellationToken.None;
      var totalFiles = fileItems.Count;
      var processed = 0;
      var progressLock = new object();

      var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
      var reader = new MetadataReader();

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

            itemWithIndex.Item.TargetLocation = targetLocation;

            // Populate the search index alongside the target calc so the
            // user can find files by keyword/person/location/etc. without a
            // second pass over the library.
            if (itemWithIndex.Item.FileInfo is { } fi && fi.Exists) {
              try {
                var metadata = await reader.ReadAsync(fi, ct);
                itemWithIndex.Item.SearchIndex = BuildFullSearchIndex(itemWithIndex.Item, metadata);
                itemWithIndex.Item.Rating = metadata.Rating;
                itemWithIndex.Item.ColorLabel = metadata.ColorLabel;
              } catch {
                // Bad metadata on one file shouldn't invalidate the whole index.
              }
            }

            lock (progressLock) {
              processed++;
              var updateInterval = totalFiles > 1000 ? 50 : 10;
              if (processed % updateInterval == 0 || processed == totalFiles) {
                var progressPercent = (int)((double)processed / totalFiles * 100);
                _ = Task.Run(() => {
                  viewModel.StatusMessage = processed == totalFiles
                    ? $"Ready. {totalFiles} files indexed."
                    : $"Indexing {processed}/{totalFiles} ({progressPercent}%)";
                });
              }
            }
          } finally {
            semaphore.Release();
          }
        }
      );

      viewModel.StatusMessage = $"Ready. {fileItems.Count} files indexed.";

    } catch (OperationCanceledException) {
      viewModel.StatusMessage = "Target calculation cancelled.";
    } catch (Exception ex) {
      viewModel.StatusMessage = $"Target calculation error: {ex.Message}";
    }
  }

  /// <summary>
  /// Re-snapshot a single FileItemModel against fresh metadata. Used after
  /// any metadata write (Save / Apply / region accept / etc.) so the
  /// in-memory rating + label + search index don't lag behind the file on
  /// disk — otherwise rating chips and the search bar would still see the
  /// pre-edit state.
  /// </summary>
  public void RefreshFileIndex(FileItemModel item, FullMetadata metadata) {
    if (item == null)
      return;
    item.SearchIndex = BuildFullSearchIndex(item, metadata);
    item.Rating = metadata.Rating;
    item.ColorLabel = metadata.ColorLabel;
  }

  /// <summary>
  /// Flatten every searchable field into a single lower-cased string so the
  /// main-window search bar can do a cheap multi-token CONTAINS per row.
  /// Includes filename, folder, keywords, region labels (people / objects /
  /// etc.), all location fields, title, caption, creator.
  /// </summary>
  private static string BuildFullSearchIndex(FileItemModel item, FullMetadata metadata) {
    var parts = new List<string> {
      item.FileName,
      item.SourcePath,
      item.FileInfo?.FullName ?? string.Empty,
      metadata.Title ?? string.Empty,
      metadata.Caption ?? string.Empty,
      metadata.Location ?? string.Empty,
      metadata.City ?? string.Empty,
      metadata.State ?? string.Empty,
      metadata.Country ?? string.Empty,
      metadata.CountryCode ?? string.Empty,
      metadata.Creator ?? string.Empty,
      metadata.Headline ?? string.Empty,
    };
    parts.AddRange(metadata.Keywords);
    parts.AddRange(metadata.PersonsShown);
    foreach (var region in metadata.Regions)
      if (region.Label is { Length: > 0 } l)
        parts.Add(l);

    return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant();
  }
}
