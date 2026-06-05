using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Lists photo bursts detected from the main window's currently scanned
/// file list. The user tweaks the time/name thresholds, ticks the bursts
/// they want to tag, and clicks Apply — every member of each ticked burst
/// receives a <c>pm:burstId=...</c> keyword via the metadata writer so
/// downstream filtering can collapse the stack.
/// </summary>
public partial class BurstStacksWindow : Window {
  private readonly IMetadataReader _reader = new MetadataReader();
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();
  private readonly IReadOnlyList<FileItemModel> _items;
  private readonly ObservableCollection<BurstGroupViewModel> _groups = new();

  public BurstStacksWindow() : this(Array.Empty<FileItemModel>()) { }

  public BurstStacksWindow(IReadOnlyList<FileItemModel> items) {
    this.InitializeComponent();
    this._items = items;

    if (this.FindControl<DataGrid>("BurstsGrid") is { } grid)
      grid.ItemsSource = this._groups;

    this.Opened += async (_, _) => await this.RedetectAsync();
  }

  private async void OnRedetectClick(object? sender, RoutedEventArgs e) => await this.RedetectAsync();

  private async Task RedetectAsync() {
    if (this._items.Count == 0) {
      this.SetStatus("No files loaded — scan your library in the main window first.");
      return;
    }

    this.SetStatus("Reading capture dates...");

    var window = TimeSpan.FromSeconds((double)(this.FindControl<NumericUpDown>("WindowSecondsBox")?.Value ?? 2m));
    var threshold = (int)(this.FindControl<NumericUpDown>("NameDistanceBox")?.Value ?? 3m);

    var entries = new List<(FileInfo, FullMetadata)>(this._items.Count);
    foreach (var item in this._items) {
      if (item.FileInfo is not { Exists: true } file)
        continue;
      FullMetadata md;
      try {
        md = await this._reader.ReadAsync(file);
      } catch {
        md = new FullMetadata();
      }
      entries.Add((file, md));
    }

    var bursts = BurstGrouper.GroupBursts(entries, window, threshold);

    Dispatcher.UIThread.Post(() => {
      this._groups.Clear();
      foreach (var b in bursts)
        this._groups.Add(new BurstGroupViewModel(b));

      var multi = bursts.Count(b => b.Members.Count > 1);
      this.SetStatus($"Detected {bursts.Count} group(s); {multi} multi-frame burst(s) across {entries.Count} file(s).");
    });
  }

  private void OnSelectMultiFrameClick(object? sender, RoutedEventArgs e) {
    foreach (var g in this._groups)
      g.IsSelected = g.Count > 1;
  }

  private async void OnApplyClick(object? sender, RoutedEventArgs e) {
    var selected = this._groups.Where(g => g.IsSelected && !string.IsNullOrWhiteSpace(g.BurstId)).ToList();
    if (selected.Count == 0) {
      this.SetStatus("Tick at least one burst with a non-empty id, then click Apply.");
      return;
    }

    this.SetStatus($"Writing burst keywords to {selected.Sum(s => s.Count)} photo(s)...");

    int filesWritten = 0;
    int errors = 0;
    foreach (var vm in selected) {
      var keyword = $"pm:burstId={vm.BurstId.Trim()}";
      foreach (var file in vm.Group.Members) {
        if (!file.Exists)
          continue;
        try {
          var current = await this._reader.ReadAsync(file);
          var keywords = current.Keywords.ToList();
          // Replace any existing pm:burstId so the burst membership stays canonical.
          keywords.RemoveAll(k => k.StartsWith("pm:burstId=", StringComparison.OrdinalIgnoreCase));
          keywords.Add(keyword);
          var patch = new MetadataEdit { Keywords = keywords };
          await this._writer.ApplyAsync(file, patch);
          filesWritten++;
        } catch {
          errors++;
        }
      }
    }

    this.SetStatus(errors == 0
      ? $"Wrote burst ids to {filesWritten} photo(s)."
      : $"Wrote {filesWritten} photo(s); {errors} failed.");
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
