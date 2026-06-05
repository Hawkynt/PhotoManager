using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Gpx;
using Hawkynt.PhotoManager.Core.Interfaces;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Apply <see cref="RenameTokenExpander"/> to a selection of files. Per-file
/// metadata is read once on open; live preview updates on every keystroke
/// in the template box. Apply renames in-place via <c>FileInfo.MoveTo</c>;
/// collisions are reported in the Status column unless the user opts in
/// to overwrite.
/// </summary>
public partial class BatchRenameWindow : Window {
  private readonly IReadOnlyList<RenameRow> _rows;
  private readonly IMetadataReader? _metadataReader;

  public BatchRenameWindow() : this(Array.Empty<FileInfo>(), null) { }

  public BatchRenameWindow(IReadOnlyList<FileInfo> files, IMetadataReader? metadataReader) {
    this.InitializeComponent();

    this._metadataReader = metadataReader;
    var rows = new List<RenameRow>(files.Count);
    foreach (var f in files) {
      // Capture date is sync + cheap; metadata is read async after the window
      // opens so we never block the UI thread (sync-over-async on the dialog's
      // owning dispatcher used to deadlock when the reader's continuation
      // posted back onto the same thread).
      var when = PhotoTimestampReader.ReadLocalCameraTime(f);
      rows.Add(new RenameRow {
        File = f,
        Metadata = null,
        CaptureDate = when,
        CurrentName = f.Name
      });
    }
    this._rows = rows;

    if (this.FindControl<DataGrid>("PreviewGrid") is { } grid)
      grid.ItemsSource = new ObservableCollection<RenameRow>(this._rows);

    if (this.FindControl<TextBox>("TemplateBox") is { } box) {
      box.TextChanged += (_, _) => this.RefreshPreview();
      this.RefreshPreview();
    }

    this.Opened += (_, _) => _ = this.LoadMetadataAsync();
  }

  private async Task LoadMetadataAsync() {
    if (this._metadataReader is not { } loader)
      return;

    foreach (var row in this._rows) {
      try {
        // Run on the thread pool — the reader's awaits would otherwise
        // capture the UI sync context and deadlock with this very dialog.
        var meta = await Task.Run(() => loader.ReadAsync(row.File));
        await Dispatcher.UIThread.InvokeAsync(() => row.Metadata = meta);
      } catch {
        // File unreadable; leave Metadata null so tokens expand to empty.
      }
    }
    await Dispatcher.UIThread.InvokeAsync(this.RefreshPreview);
  }

  private void RefreshPreview() {
    var template = this.FindControl<TextBox>("TemplateBox")?.Text ?? "";
    if (string.IsNullOrWhiteSpace(template)) {
      foreach (var row in this._rows) { row.NewName = row.CurrentName; row.Status = "(template empty)"; }
      this.RefreshGrid();
      return;
    }

    var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < this._rows.Count; i++) {
      var row = this._rows[i];
      var stem = RenameTokenExpander.Expand(template, row.File, row.Metadata, row.CaptureDate, i + 1, this._rows.Count);
      // Re-attach the original extension if the template didn't include one.
      if (!Path.HasExtension(stem) && !string.IsNullOrEmpty(row.File.Extension))
        stem += row.File.Extension;
      row.NewName = stem;

      if (string.IsNullOrWhiteSpace(stem)) {
        row.Status = "empty result";
      } else if (string.Equals(stem, row.CurrentName, StringComparison.OrdinalIgnoreCase)) {
        row.Status = "no change";
      } else if (!seenNames.Add(stem)) {
        row.Status = "duplicate within batch";
      } else {
        var dest = Path.Combine(row.File.DirectoryName ?? string.Empty, stem);
        row.Status = File.Exists(dest) ? "exists on disk" : "ready";
      }
    }
    this.RefreshGrid();
  }

  private void RefreshGrid() {
    if (this.FindControl<DataGrid>("PreviewGrid") is { } grid) {
      // Force re-binding so the grid picks up updated NewName / Status fields.
      grid.ItemsSource = null;
      grid.ItemsSource = new ObservableCollection<RenameRow>(this._rows);
    }
  }

  private async void OnApplyClick(object? sender, RoutedEventArgs e) {
    var overwrite = this.FindControl<CheckBox>("OverwriteCheck")?.IsChecked == true;
    var renamed = 0;
    var skipped = 0;
    var failed = 0;

    foreach (var row in this._rows) {
      if (string.IsNullOrWhiteSpace(row.NewName)
          || string.Equals(row.NewName, row.CurrentName, StringComparison.OrdinalIgnoreCase)) {
        row.Status = "skipped";
        skipped++;
        continue;
      }
      var dir = row.File.DirectoryName;
      if (string.IsNullOrEmpty(dir)) { row.Status = "no dir"; failed++; continue; }
      var dest = Path.Combine(dir, row.NewName);

      try {
        if (File.Exists(dest)) {
          if (!overwrite) { row.Status = "skipped (exists)"; skipped++; continue; }
          File.Delete(dest);
        }
        // Refresh after move so subsequent renames see the new path.
        row.File.MoveTo(dest);
        row.CurrentName = row.NewName;
        row.Status = "renamed";
        renamed++;
      } catch (Exception ex) {
        row.Status = "failed: " + ex.Message;
        failed++;
      }
    }

    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = $"Renamed {renamed}, skipped {skipped}, failed {failed}.";
    this.RefreshGrid();
    await Task.CompletedTask;
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close();
}
