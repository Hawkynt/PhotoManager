using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Hawkynt.PhotoManager.Core.Develop;
using Hawkynt.PhotoManager.UI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Read-only viewer for the develop history stack. Each row is one prior
/// <see cref="DevelopSnapshot"/> rendered against the source-file preview;
/// users restore by selecting + clicking Restore (the <see cref="EditImageWindow"/>
/// applies the snapshot to its sliders without writing yet) or prune by
/// clicking Delete. Closing the window without restoring leaves the live
/// settings untouched.
/// </summary>
public partial class EditHistoryWindow : Window {
  private readonly FileInfo? _sourceFile;
  private readonly int _copyIndex;
  private readonly Image<Rgba32>? _previewSource;
  private readonly ObservableCollection<HistorySnapshotRow> _rows = new();
  private List<DevelopSnapshot> _snapshots = new();

  /// <summary>
  /// The snapshot the user picked Restore on, or null when they cancelled
  /// or clicked Close. Read by the caller after <see cref="Window.ShowDialog"/>
  /// returns.
  /// </summary>
  public DevelopSnapshot? RestoredSnapshot { get; private set; }

  public EditHistoryWindow() : this(null, 0, null, Array.Empty<DevelopSnapshot>()) { }

  public EditHistoryWindow(FileInfo? sourceFile, int copyIndex, Image<Rgba32>? previewSource, IReadOnlyList<DevelopSnapshot> initial) {
    this.InitializeComponent();
    this._sourceFile = sourceFile;
    this._copyIndex = copyIndex;
    this._previewSource = previewSource;
    this._snapshots = initial.ToList();

    if (this.FindControl<ListBox>("SnapshotList") is { } list)
      list.ItemsSource = this._rows;
    if (this.FindControl<TextBlock>("HeaderText") is { } header && sourceFile is not null)
      header.Text = copyIndex == 0
        ? $"Develop snapshots — {sourceFile.Name}"
        : $"Develop snapshots — {sourceFile.Name} (copy {copyIndex})";

    this.RefreshRows();
  }

  private void RefreshRows() {
    this._rows.Clear();
    for (var i = 0; i < this._snapshots.Count; i++) {
      var thumb = this.RenderThumbnail(this._snapshots[i].Settings);
      this._rows.Add(new HistorySnapshotRow(i, this._snapshots[i], thumb));
    }
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = this._snapshots.Count == 0
        ? "No snapshots yet."
        : $"{this._snapshots.Count} snapshot(s)";
  }

  private Bitmap? RenderThumbnail(DevelopSettings settings) {
    if (this._previewSource is null)
      return null;
    try {
      using var clone = this._previewSource.Clone();
      // Down-scale before applying the develop pipeline so iterating through
      // 20 snapshots stays cheap even on big preview sources.
      var maxEdge = Math.Max(clone.Width, clone.Height);
      if (maxEdge > 160) {
        var scale = 160.0 / maxEdge;
        clone.Mutate(c => c.Resize((int)(clone.Width * scale), (int)(clone.Height * scale)));
      }
      using var developed = ImageDeveloper.Apply(clone, settings, previewMode: true);
      using var ms = new MemoryStream();
      developed.SaveAsJpeg(ms);
      ms.Position = 0;
      return new Bitmap(ms);
    } catch {
      return null;
    }
  }

  private void OnRestoreClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<ListBox>("SnapshotList")?.SelectedItem is not HistorySnapshotRow row) {
      this.SetStatus("Pick a snapshot first.");
      return;
    }
    this.RestoredSnapshot = row.Snapshot;
    this.Close();
  }

  private async void OnDeleteClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<ListBox>("SnapshotList")?.SelectedItem is not HistorySnapshotRow row) {
      this.SetStatus("Pick a snapshot to delete.");
      return;
    }
    if (this._sourceFile is null) {
      this._snapshots.RemoveAt(row.Index);
      this.RefreshRows();
      return;
    }
    var keep = this._snapshots.Where((_, i) => i != row.Index).ToList();
    var ok = await DevelopMetadataStore.RewriteHistoryAsync(this._sourceFile, this._copyIndex, keep);
    if (!ok) {
      this.SetStatus("Failed to update history.");
      return;
    }
    this._snapshots = keep;
    this.RefreshRows();
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
