using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using PhotoManager.UI.Resources;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

public partial class MainWindow : Window {
  private const string UiDateFormat = "dd.MM.yyyy HH:mm:ss";
  private readonly MainController _controller;
  private readonly AboutController _aboutController;
  private ObservableCollection<FileItemModel>? _currentFileItems;
  private CancellationTokenSource? _previewCts;

  public MainWindow() : this(null!, null!) { }

  public MainWindow(MainController controller, AboutController aboutController) {
    this._controller = controller;
    this._aboutController = aboutController;

    this.InitializeComponent();

    if (controller == null)
      return;

    this.DataContext = controller.ViewModel;

    var combo = this.FindControl<ComboBox>("DuplicateHandlingCombo");
    if (combo != null)
      combo.ItemsSource = Enum.GetValues<DuplicateHandling>();

    var tree = this.FindControl<TreeView>("SourceTree");
    if (tree != null)
      tree.ItemsSource = controller.SourceTreeRoots;

    this.Opened += this.OnWindowOpened;
  }

  private async void OnWindowOpened(object? sender, EventArgs e)
    => await this._controller.LoadSettingsAsync();

  private async void OnBrowseDestinationClick(object? sender, RoutedEventArgs e)
    => await this._controller.SelectDestinationDirectoryAsync();

  private async void OnAddPathClick(object? sender, RoutedEventArgs e)
    => await this._controller.AddSourcePathInteractiveAsync();

  private void OnRemovePathClick(object? sender, RoutedEventArgs e) {
    if (this.GetSelectedRoot() is { } root)
      this._controller.RemoveSourceTreeRoot(root);
  }

  private void OnToggleRecursiveClick(object? sender, RoutedEventArgs e) {
    if (this.GetSelectedRoot() is { } root)
      this._controller.ToggleRecursive(root);
  }

  private SourceTreeNode? GetSelectedRoot() {
    var tree = this.FindControl<TreeView>("SourceTree");
    if (tree?.SelectedItem is not SourceTreeNode node)
      return null;

    return node.IsRoot ? node : null;
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var grid = this.FindControl<DataGrid>("FilesGrid");
    if (grid != null)
      grid.ItemsSource = null;

    var items = await this._controller.ScanCheckedSourcesAsync();
    this._currentFileItems = items;

    if (grid != null)
      grid.ItemsSource = items;
  }

  private async void OnRunClick(object? sender, RoutedEventArgs e) {
    if (this._currentFileItems == null || this._currentFileItems.Count == 0)
      return;

    await this._controller.ProcessSelectedFilesAsync(this._currentFileItems);
  }

  private async void OnRunDirectoryClick(object? sender, RoutedEventArgs e)
    => await this._controller.ProcessDirectoryAsync();

  private void OnCancelClick(object? sender, RoutedEventArgs e)
    => this._controller.CancelProcessing();

  private async void OnAboutClick(object? sender, RoutedEventArgs e) {
    var window = new AboutWindow(this._aboutController);
    await window.ShowDialog(this);
  }

  private async void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (sender is not DataGrid grid || grid.SelectedItem is not FileItemModel { FileInfo.Exists: true } fileItem)
      return;

    this._previewCts?.Cancel();
    this._previewCts = new CancellationTokenSource();
    var token = this._previewCts.Token;

    await this.UpdatePreviewAsync(fileItem.FileInfo!, token);
    if (token.IsCancellationRequested)
      return;

    await this.UpdateMetadataAsync(fileItem.FileInfo!, token);
  }

  private async Task UpdatePreviewAsync(FileInfo file, CancellationToken token) {
    var preview = this.FindControl<Image>("PreviewImage");
    if (preview == null)
      return;

    var bitmap = await ImagePreviewLoader.LoadAsync(file, token);
    if (token.IsCancellationRequested)
      return;

    preview.Source = bitmap;
  }

  private async Task UpdateMetadataAsync(FileInfo file, CancellationToken token) {
    var list = this.FindControl<ListBox>("MetadataList");
    if (list == null)
      return;

    var fileToImport = new FileToImport(file);

    DateTime? exifOriginal = null;
    DateTime? exifModified = null;
    DateTime? gps = null;
    DateTime? filename = null;

    try {
      await foreach (var d in fileToImport.GetExifSubIfdDateAsync()) { exifOriginal = d; break; }
      await foreach (var d in fileToImport.GetExifIfd0DateAsync())   { exifModified = d; break; }
      await foreach (var d in fileToImport.GetGpsDateAsync())        { gps = d; break; }

      var parser = new DateTimeParser();
      var settings = new ImportSettings();
      await foreach (var d in parser.ParseDateFromFileName(fileToImport, settings)) { filename = d; break; }
    } catch {
      // Ignore metadata extraction errors — the grid just shows "Not found"
    }

    if (token.IsCancellationRequested)
      return;

    var (_, winning) = await this._controller.GetMostLogicalDateWithSourceAsync(fileToImport);
    if (token.IsCancellationRequested)
      return;

    var rows = new List<MetadataRow> {
      Row("File Name",     file.Name, DateTimeSource.Unknown, winning),
      Row("File Path",     file.FullName, DateTimeSource.Unknown, winning),
      Row("File Size",     FormatFileSize(file.Length), DateTimeSource.Unknown, winning),
      Row("Date Created",  file.CreationTime.ToString(UiDateFormat), DateTimeSource.FileCreatedAt, winning),
      Row("Date Modified", file.LastWriteTime.ToString(UiDateFormat), DateTimeSource.FileModifiedAt, winning),
      Row("EXIF Original", exifOriginal?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.ExifSubIfd, winning),
      Row("EXIF Modified", exifModified?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.ExifIfd0, winning),
      Row("GPS Date",      gps?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.Gps, winning),
      Row("Filename Date", filename?.ToString(UiDateFormat) ?? Strings.Metadata_NotDetected, DateTimeSource.FileName, winning),
    };

    list.ItemsSource = rows;
    this.ApplyMetadataTinting(list, rows);
  }

  private void ApplyMetadataTinting(ListBox list, IReadOnlyList<MetadataRow> rows) {
    list.ContainerPrepared -= this.OnMetadataContainerPrepared;
    list.ContainerPrepared += this.OnMetadataContainerPrepared;

    for (var i = 0; i < list.ItemCount; i++) {
      if (list.ContainerFromIndex(i) is ListBoxItem item)
        ApplyRowClasses(item, rows[i]);
    }
  }

  private void OnMetadataContainerPrepared(object? sender, ContainerPreparedEventArgs e) {
    if (e.Container is ListBoxItem item && item.DataContext is MetadataRow row)
      ApplyRowClasses(item, row);
  }

  private static void ApplyRowClasses(ListBoxItem item, MetadataRow row) {
    item.Classes.Remove("winner");
    item.Classes.Remove("missing");
    if (row.IsWinner)
      item.Classes.Add("winner");
    else if (row.IsMissing)
      item.Classes.Add("missing");
  }

  private static MetadataRow Row(string name, string value, DateTimeSource source, DateTimeSource winning) {
    var missing = value == Strings.Metadata_NotFound || value == Strings.Metadata_NotDetected;
    var isWinner = source != DateTimeSource.Unknown && source == winning;
    return new MetadataRow(name, value, isWinner, missing);
  }

  private static string FormatFileSize(long bytes) {
    string[] sizes = [Strings.FileSize_Bytes, Strings.FileSize_Kilobytes, Strings.FileSize_Megabytes, Strings.FileSize_Gigabytes];
    double len = bytes;
    var order = 0;
    while (len >= 1024 && order < sizes.Length - 1) {
      ++order;
      len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
  }

  private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) {
    var grid = this.FindControl<DataGrid>("FilesGrid");
    if (grid?.SelectedItem is not FileItemModel { FileInfo.Exists: true } fileItem)
      return;

    try {
      ShellLauncher.OpenInDefaultViewer(fileItem.FileInfo!.FullName);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = string.Format(Strings.Error_CouldNotOpenImage, ex.Message);
    }
  }
}
