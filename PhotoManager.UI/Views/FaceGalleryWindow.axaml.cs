using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Previews;
using PhotoManager.Core.Regions;
using PhotoManager.Core.Services;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Picasa-style gallery window. The user picks a folder; we walk it via
/// <see cref="LibraryFaceScanner"/>, cluster the embedded faces via
/// <see cref="FaceClusterIndex"/>, and show one tile per cluster. Naming
/// a cluster writes the label back onto every member's XMP (in-file or
/// sidecar) via <see cref="CompositeMetadataWriter"/>, travelling with the
/// photos just like any other tag.
///
/// Merging is user-driven: tick the "select" checkbox on two or more
/// clusters and click "Merge selected" to union them under the first
/// named cluster (or the largest one if nothing is named yet).
/// </summary>
public partial class FaceGalleryWindow : Window {
  private readonly LibraryFaceScanner _scanner = new(new MetadataReader(), new SupportedFormatsService());
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();
  private readonly IMetadataReader _reader = new MetadataReader();

  private readonly List<FaceClusterViewModel> _clusterViewModels = new();
  private IReadOnlyList<ScannedFace> _lastScan = Array.Empty<ScannedFace>();
  private CancellationTokenSource? _scanCts;

  public FaceGalleryWindow() {
    this.InitializeComponent();
    if (this.FindControl<ItemsControl>("ClustersList") is { } list)
      list.ItemsSource = this._clusterViewModels;
  }

  public FaceGalleryWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select library folder", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a folder first.");
      return;
    }

    this._scanCts?.Cancel();
    this._scanCts = new CancellationTokenSource();
    var token = this._scanCts.Token;

    this._clusterViewModels.Clear();
    if (this.FindControl<ItemsControl>("ClustersList") is { } list)
      list.ItemsSource = null;

    this.SetStatus("Scanning...");

    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;
    var progress = new Progress<FileInfo>(f =>
      Dispatcher.UIThread.Post(() => this.SetStatus($"Reading {f.Name}..."))
    );

    IReadOnlyList<ScannedFace> faces;
    FaceClusterIndex index;
    try {
      faces = await this._scanner.ScanAsync(
        new DirectoryInfo(folder),
        recursive: recursive,
        onlyEmbedded: false,
        progress: progress,
        cancellationToken: token
      );
      // Pass the full set — faces WITH embeddings cluster together; faces
      // WITHOUT get surfaced as singletons so the gallery is useful even
      // when the ArcFace model isn't installed.
      index = FaceClusterIndex.Build(faces);
    } catch (OperationCanceledException) {
      this.SetStatus("Scan cancelled.");
      return;
    } catch (Exception ex) {
      this.SetStatus($"Scan failed: {ex.Message}");
      return;
    }

    this._lastScan = faces;
    this.PopulateClusters(index);

    var withEmbedding = faces.Count(f => f.HasEmbedding);
    var hint = withEmbedding == 0 && faces.Count > 0
      ? " (no embeddings — install the ArcFace model to enable automatic grouping)"
      : "";
    this.SetStatus(
      $"Found {faces.Count} face(s); {withEmbedding} with embeddings → {index.Clusters.Count} cluster(s).{hint}"
    );
  }

  private void PopulateClusters(FaceClusterIndex index) {
    this._clusterViewModels.Clear();

    foreach (var cluster in index.Clusters)
      this._clusterViewModels.Add(new FaceClusterViewModel(cluster));

    // Force a full rebind — assigning the same List reference wouldn't
    // re-trigger the ItemsControl since a plain List doesn't raise
    // collection-change notifications.
    if (this.FindControl<ItemsControl>("ClustersList") is { } list) {
      list.ItemsSource = null;
      list.ItemsSource = this._clusterViewModels;
    }

    _ = this.PopulateThumbnailsAsync();
  }

  private async Task PopulateThumbnailsAsync() {
    foreach (var vm in this._clusterViewModels.ToList()) {
      // Representative thumbnail = first member (clusters are ordered so the
      // representative is stable across re-renders).
      var first = vm.Cluster.Members.FirstOrDefault();
      if (first == null)
        continue;

      byte[]? bytes;
      try {
        bytes = await RegionThumbnailExtractor.CropAsync(first.File, first.Region.Box);
      } catch {
        bytes = null;
      }
      if (bytes == null)
        continue;

      // Marshal the bitmap assignment to the UI thread explicitly. Even
      // though the continuation normally captures the dispatcher, doing it
      // by hand makes the assumption explicit and survives edge cases where
      // the await context was lost.
      try {
        var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
        Dispatcher.UIThread.Post(() => vm.Thumbnail = bitmap);
      } catch {
        // Ignore — a missing thumbnail doesn't break the workflow.
      }
    }
  }

  private async void OnApplyNameClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: FaceClusterViewModel vm })
      return;
    var name = (vm.Name ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(name)) {
      this.SetStatus("Enter a name before applying.");
      return;
    }

    this.SetStatus($"Applying \"{name}\" to {vm.Count} photo(s)...");
    try {
      var written = await this.WriteNameToClusterAsync(vm.Cluster, name);
      this.SetStatus($"Wrote name \"{name}\" to {written} photo(s).");
    } catch (Exception ex) {
      this.SetStatus($"Apply failed: {ex.Message}");
    }
  }

  private async Task<int> WriteNameToClusterAsync(FaceCluster cluster, string name) {
    // Group by file so each file is written only once regardless of how many
    // regions it contributes to the cluster.
    var byFile = cluster.Members.GroupBy(m => m.File.FullName, StringComparer.OrdinalIgnoreCase);
    var written = 0;

    foreach (var group in byFile) {
      var file = new FileInfo(group.Key);
      if (!file.Exists)
        continue;

      var current = await this._reader.ReadAsync(file);
      var targetBoxes = group.Select(g => g.Region.Box).ToHashSet();

      var updatedRegions = current.Regions
        .Select(r => targetBoxes.Contains(r.Box) && r.Category == RegionCategory.Person
          ? r with { Label = name, Status = RegionStatus.Accepted }
          : r)
        .ToList();

      // Keywords gain the name automatically on save (we just ensure it's present
      // — CompositeMetadataWriter doesn't auto-promote labels to keywords).
      var keywords = current.Keywords.ToList();
      if (!keywords.Contains(name, StringComparer.OrdinalIgnoreCase))
        keywords.Add(name);

      var patch = new MetadataEdit {
        Regions = updatedRegions,
        Keywords = keywords
      };

      await this._writer.ApplyAsync(file, patch);
      written++;
    }

    return written;
  }

  private async void OnMergeSelectedClick(object? sender, RoutedEventArgs e) {
    var selected = this._clusterViewModels.Where(c => c.IsSelected).ToList();
    if (selected.Count < 2) {
      this.SetStatus("Tick two or more clusters to merge.");
      return;
    }

    // Pick the dominant name: first named cluster in the selection. If none
    // is named, use the largest cluster's size as the seed and leave it
    // unnamed — the user can name the merged group afterwards.
    var named = selected.FirstOrDefault(c => c.IsNamed);
    var targetName = named?.Cluster.Label ?? named?.Name;
    if (string.IsNullOrWhiteSpace(targetName))
      targetName = selected.Select(c => c.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    this.SetStatus(targetName == null
      ? "Merging clusters (unnamed)..."
      : $"Merging {selected.Count} clusters under \"{targetName}\"...");

    var merged = selected.SelectMany(c => c.Cluster.Members).ToList();

    if (!string.IsNullOrWhiteSpace(targetName)) {
      try {
        var written = await this.WriteNameToClusterAsync(
          new FaceCluster(0, merged), targetName
        );
        this.SetStatus($"Merged into \"{targetName}\"; {written} photo(s) updated.");
      } catch (Exception ex) {
        this.SetStatus($"Merge write failed: {ex.Message}");
        return;
      }
    } else {
      // No name yet: collapse the VM list locally so the user sees the merge.
      // The real XMP merge happens when they name-and-apply the combined group.
      var first = selected[0];
      var combined = new FaceCluster(first.Cluster.Id, merged);
      var mergedVm = new FaceClusterViewModel(combined);
      foreach (var c in selected)
        this._clusterViewModels.Remove(c);
      this._clusterViewModels.Insert(0, mergedVm);
      if (this.FindControl<ItemsControl>("ClustersList") is { } list) {
        list.ItemsSource = null;
        list.ItemsSource = this._clusterViewModels;
      }
      _ = this.PopulateThumbnailsAsync();
      this.SetStatus($"Merged {selected.Count} clusters locally — enter a name and click Apply to persist.");
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
