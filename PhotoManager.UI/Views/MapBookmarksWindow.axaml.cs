using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// CRUD UI for <see cref="MapBookmark"/>s. Loads on open, mutates an
/// in-memory <see cref="ObservableCollection{T}"/>, persists on Save (or
/// implicitly on Apply / Close-after-edit). Apply-to-selection writes the
/// bookmark's GPS + place fields onto every file currently selected in the
/// main window via the supplied callback so this dialog stays decoupled
/// from MainWindow's internals.
/// </summary>
public partial class MapBookmarksWindow : Window {
  private readonly MapBookmarkStore _store;
  private readonly ObservableCollection<MapBookmarkRow> _rows = new();
  private readonly Func<IReadOnlyList<FileInfo>>? _selectionProvider;
  private readonly IMetadataWriter _writer;
  private readonly Func<FileInfo, Task>? _afterApply;

  public MapBookmarksWindow() : this(null, null, null, null) { }

  public MapBookmarksWindow(
    MapBookmarkStore? store,
    Func<IReadOnlyList<FileInfo>>? selectionProvider,
    IMetadataWriter? writer,
    Func<FileInfo, Task>? afterApply
  ) {
    this.InitializeComponent();
    this._store = store ?? new MapBookmarkStore();
    this._selectionProvider = selectionProvider;
    this._writer = writer ?? new CompositeMetadataWriter();
    this._afterApply = afterApply;

    if (this.FindControl<DataGrid>("BookmarksGrid") is { } grid)
      grid.ItemsSource = this._rows;

    this.LoadFromStore();
  }

  private void LoadFromStore() {
    this._rows.Clear();
    foreach (var b in this._store.Load())
      this._rows.Add(MapBookmarkRow.FromBookmark(b));
    this.SetStatus($"{this._rows.Count} bookmark(s) loaded.");
  }

  private async void OnAddClick(object? sender, RoutedEventArgs e) {
    var dialog = new InputDialogWindow("Add bookmark", "Bookmark name:");
    var name = await dialog.ShowDialog<string?>(this);
    if (string.IsNullOrWhiteSpace(name))
      return;

    var row = new MapBookmarkRow { Name = name.Trim() };
    this._rows.Add(row);
    if (this.FindControl<DataGrid>("BookmarksGrid") is { } grid) {
      grid.SelectedItem = row;
      grid.ScrollIntoView(row, null);
    }
    this.SetStatus($"Added \"{row.Name}\". Use Pick on map… to set its coordinates.");
  }

  private async void OnEditClick(object? sender, RoutedEventArgs e) {
    if (this.SelectedRow() is not { } row) {
      this.SetStatus("Select a bookmark first.");
      return;
    }

    var dialog = new InputDialogWindow("Rename bookmark", "Bookmark name:", row.Name);
    var name = await dialog.ShowDialog<string?>(this);
    if (string.IsNullOrWhiteSpace(name))
      return;

    row.Name = name.Trim();
    this.SetStatus($"Renamed to \"{row.Name}\".");
  }

  private async void OnPickOnMapClick(object? sender, RoutedEventArgs e) {
    if (this.SelectedRow() is not { } row) {
      this.SetStatus("Select a bookmark first.");
      return;
    }

    GpsCoordinate? initial = null;
    if (row.Latitude is >= -90.0 and <= 90.0 && row.Longitude is >= -180.0 and <= 180.0
        && (row.Latitude != 0 || row.Longitude != 0))
      initial = new GpsCoordinate(row.Latitude, row.Longitude);

    var picker = new MapPickerWindow(initialCamera: initial);
    var result = await picker.ShowDialog<MapPickerWindow.Result?>(this);
    if (result?.Camera is not { } camera)
      return;

    row.Latitude = camera.Latitude;
    row.Longitude = camera.Longitude;
    this.SetStatus($"Pin set to {camera.Latitude:0.######}, {camera.Longitude:0.######}.");
  }

  private void OnDeleteClick(object? sender, RoutedEventArgs e) {
    if (this.SelectedRow() is not { } row) {
      this.SetStatus("Select a bookmark first.");
      return;
    }
    this._rows.Remove(row);
    this.SetStatus($"Deleted \"{row.Name}\".");
  }

  private void OnSaveClick(object? sender, RoutedEventArgs e) {
    this.SaveToStore();
    this.SetStatus($"Saved {this._rows.Count} bookmark(s).");
  }

  private async void OnApplyToSelectionClick(object? sender, RoutedEventArgs e) {
    if (this.SelectedRow() is not { } row) {
      this.SetStatus("Select a bookmark first.");
      return;
    }
    if (string.IsNullOrWhiteSpace(row.Name)) {
      this.SetStatus("Give the bookmark a name first.");
      return;
    }

    var bookmark = row.ToBookmark();
    if (!new GpsCoordinate(bookmark.Latitude, bookmark.Longitude).IsValid) {
      this.SetStatus("Bookmark has no valid coordinates — pick them on the map first.");
      return;
    }

    var files = this._selectionProvider?.Invoke() ?? Array.Empty<FileInfo>();
    if (files.Count == 0) {
      this.SetStatus("Select one or more files in the main window first.");
      return;
    }

    // Persist before applying so a write failure doesn't leave the user
    // wondering whether their bookmark survived.
    this.SaveToStore();

    var edit = MapBookmarkApplier.BuildEdit(bookmark);
    var written = 0;
    var errors = 0;
    foreach (var file in files) {
      if (!file.Exists) {
        errors++;
        continue;
      }
      try {
        await this._writer.ApplyAsync(file, edit);
        written++;
        if (this._afterApply is not null)
          await this._afterApply(file);
      } catch {
        errors++;
      }
    }

    this.SetStatus(errors == 0
      ? $"Applied \"{bookmark.Name}\" to {written} file(s)."
      : $"Applied to {written} file(s); {errors} failed.");
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    // Save on close so the user doesn't have to remember to click Save —
    // matches the "edits don't get lost" promise of the rest of the app.
    this.SaveToStore();
    this.Close();
  }

  private MapBookmarkRow? SelectedRow() =>
    this.FindControl<DataGrid>("BookmarksGrid")?.SelectedItem as MapBookmarkRow;

  private void SaveToStore() {
    var bookmarks = this._rows
      .Where(r => !string.IsNullOrWhiteSpace(r.Name))
      .Select(r => r.ToBookmark())
      .ToList();
    this._store.Save(bookmarks);
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
