using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Faces;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Previews;
using Hawkynt.PhotoManager.Core.Regions;
using Hawkynt.PhotoManager.Core.Services;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Picasa-style gallery window. Consumes the main window's scanned file
/// list, reads face regions via <see cref="LibraryFaceScanner"/>, clusters
/// them via <see cref="FaceClusterIndex"/>, and shows one tile per cluster.
/// Naming a cluster writes the label back onto every member's XMP (in-file
/// or sidecar) via <see cref="CompositeMetadataWriter"/>, travelling with
/// the photos just like any other tag.
///
/// Merging is user-driven: tick the "select" checkbox on two or more
/// clusters and click "Merge selected" to union them under the first
/// named cluster (or the largest one if nothing is named yet).
///
/// When "Auto-cluster by similarity" is checked, unnamed faces with
/// embeddings are automatically grouped by cosine similarity (single-linkage
/// agglomerative clustering). The "Who?" button on unnamed clusters suggests
/// the top-5 nearest named clusters by centroid distance.
/// </summary>
public partial class FaceGalleryWindow : Window {
  private readonly LibraryFaceScanner _scanner = new(new MetadataReader(), new SupportedFormatsService());
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();
  private readonly IMetadataReader _reader = new MetadataReader();

  // ObservableCollection so Clear() + Add(...) raise CollectionChanged and
  // the ItemsControl drops its stale item containers cleanly. Using a plain
  // List<T> meant repeated scans could leave ghost tiles in the UI because
  // the control didn't see the mutation.
  private readonly ObservableCollection<FaceClusterViewModel> _clusterViewModels = new();
  private IReadOnlyList<ScannedFace> _lastScan = Array.Empty<ScannedFace>();
  private FaceClusterIndex? _lastIndex;
  private CancellationTokenSource? _scanCts;
  private readonly IReadOnlyList<FileInfo> _files;

  public FaceGalleryWindow() : this(Array.Empty<FileInfo>()) { }

  /// <summary>
  /// Takes the already-scanned file list from the caller (the main window's
  /// grid). Runs the face-region scan automatically on Open so the user
  /// lands directly on the gallery — there is no manual scan button any
  /// more because the source is always the library's current file list.
  /// </summary>
  public FaceGalleryWindow(IReadOnlyList<FileInfo> files) {
    this.InitializeComponent();
    this._files = files;

    if (this.FindControl<ItemsControl>("ClustersList") is { } list)
      list.ItemsSource = this._clusterViewModels;

    this.Opened += async (_, _) => {
      if (this._files.Count == 0) {
        this.SetStatus("No files loaded — scan your library in the main window first.");
        return;
      }
      await this.RunScanAsync();
    };
  }

  private async Task RunScanAsync() {
    this._scanCts?.Cancel();
    this._scanCts = new CancellationTokenSource();
    var token = this._scanCts.Token;

    // ObservableCollection raises CollectionChanged on Clear so the
    // ItemsControl drops its old tile containers right now — no ghost
    // tiles linger into the new scan.
    this._clusterViewModels.Clear();

    this.SetStatus("Scanning...");

    var progress = new Progress<FileInfo>(f =>
      Dispatcher.UIThread.Post(() => this.SetStatus($"Reading {f.Name}..."))
    );

    IReadOnlyList<ScannedFace> faces;
    try {
      faces = await this._scanner.ScanFilesAsync(
        this._files,
        onlyEmbedded: false,
        progress: progress,
        cancellationToken: token
      );
    } catch (OperationCanceledException) {
      this.SetStatus("Scan cancelled.");
      return;
    } catch (Exception ex) {
      this.SetStatus($"Scan failed: {ex.Message}");
      return;
    }

    this._lastScan = faces;
    this.RebuildClusters();
  }

  /// <summary>
  /// Re-cluster the current scan using the latest toggle settings.
  /// No disk IO — we only rerun clustering over the in-memory scan
  /// and refresh the VMs.
  /// </summary>
  private void RebuildClusters() {
    var groupByName = this.FindControl<CheckBox>("GroupByNameBox")?.IsChecked ?? true;
    var clusterByEmbedding = this.FindControl<CheckBox>("ClusterByEmbeddingBox")?.IsChecked ?? false;

    FaceClusterIndex index;
    if (clusterByEmbedding) {
      // Embedding-based clustering: named faces group by name, unnamed
      // faces with embeddings cluster by cosine similarity.
      index = FaceClusterIndex.BuildWithEmbeddingClustering(
        this._lastScan,
        FaceClusterIndex.DefaultSimilarityThreshold
      );
    } else {
      index = FaceClusterIndex.Build(this._lastScan, groupByName: groupByName);
    }

    this._lastIndex = index;
    this.PopulateClusters(index);

    var withEmbedding = this._lastScan.Count(f => f.HasEmbedding);
    var hint = withEmbedding == 0 && this._lastScan.Count > 0
      ? " (no embeddings — install the ArcFace model to enable automatic grouping)"
      : "";
    var mode = clusterByEmbedding ? "similarity-clustered" : (groupByName ? "name-grouped" : "ungrouped");
    this.SetStatus(
      $"Found {this._lastScan.Count} face(s); {withEmbedding} with embeddings → {index.Clusters.Count} cluster(s) [{mode}].{hint}"
    );
  }

  private void OnGroupByNameChanged(object? sender, RoutedEventArgs e) {
    // Don't trigger before the first scan completes — IsChecked changes at
    // template-apply time can fire before Opened does.
    if (this._lastScan.Count == 0)
      return;
    this.RebuildClusters();
  }

  private void OnClusterModeChanged(object? sender, RoutedEventArgs e) {
    if (this._lastScan.Count == 0)
      return;
    this.RebuildClusters();
  }

  private void PopulateClusters(FaceClusterIndex index) {
    this._clusterViewModels.Clear();
    foreach (var cluster in index.Clusters)
      this._clusterViewModels.Add(new FaceClusterViewModel(cluster));

    _ = this.PopulateThumbnailsAsync();
  }

  private Task PopulateThumbnailsAsync() => this.PopulateThumbnailsAsync(this._clusterViewModels.ToList());

  private async Task PopulateThumbnailsAsync(IReadOnlyList<FaceClusterViewModel> targets) {
    foreach (var vm in targets) {
      if (vm.Thumbnail != null)
        continue;
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

  private async void OnSuggestMergeClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: FaceClusterViewModel vm })
      return;

    if (this._lastIndex == null) {
      this.SetStatus("No cluster index available — run a scan first.");
      return;
    }

    var suggestions = this._lastIndex.FindNearestNamedClusters(vm.Cluster, topN: 5);
    if (suggestions.Count == 0) {
      this.SetStatus("No named clusters with embeddings to compare against.");
      return;
    }

    var dialog = new MergeSuggestionDialog(vm.Cluster, suggestions);
    var result = await dialog.ShowDialog<MergeSuggestionResult?>(this);
    if (result == null)
      return;

    // User chose to merge this unnamed cluster into a named one.
    this.SetStatus($"Merging \"{vm.DisplayName}\" into \"{result.TargetCluster.DisplayName}\"...");

    var mergedFaces = vm.Cluster.Members.ToList();
    var targetName = result.TargetCluster.Label!;

    try {
      var written = await this.WriteNameToClusterAsync(
        new FaceCluster(0, mergedFaces), targetName
      );

      var updatedFaces = mergedFaces
        .Select(f => new ScannedFace(f.File, f.Region with { Label = targetName, Status = RegionStatus.Accepted }))
        .ToList();
      this.UpdateScannedFaces(updatedFaces);

      // Find the target VM and the source VM, merge them in the UI
      var targetVm = this._clusterViewModels.FirstOrDefault(c =>
        c.Cluster.Label != null &&
        c.Cluster.Label.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
        c != vm);

      if (targetVm != null) {
        var allMembers = targetVm.Cluster.Members.Concat(updatedFaces).ToList();
        var combinedCluster = new FaceCluster(targetVm.Cluster.Id, allMembers);
        var combinedVm = new FaceClusterViewModel(combinedCluster) {
          Name = targetName,
          Thumbnail = targetVm.Thumbnail
        };
        var idx = this._clusterViewModels.IndexOf(targetVm);
        if (idx >= 0)
          this._clusterViewModels[idx] = combinedVm;
        this._clusterViewModels.Remove(vm);
      } else {
        // Target cluster not visible — just rename the source VM
        var renamedCluster = new FaceCluster(vm.Cluster.Id, updatedFaces);
        var renamedVm = new FaceClusterViewModel(renamedCluster) {
          Name = targetName,
          Thumbnail = vm.Thumbnail
        };
        var idx = this._clusterViewModels.IndexOf(vm);
        if (idx >= 0)
          this._clusterViewModels[idx] = renamedVm;
      }

      this.SetStatus($"Merged into \"{targetName}\"; {written} photo(s) updated.");
    } catch (Exception ex) {
      this.SetStatus($"Merge failed: {ex.Message}");
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

    // Build the merged face list. If we wrote a name through, mirror the
    // name onto the in-memory faces so the collapsed VM displays it right
    // away (writer already persisted it to XMP).
    var mergedFaces = selected.SelectMany(c => c.Cluster.Members).ToList();

    if (!string.IsNullOrWhiteSpace(targetName)) {
      int written;
      try {
        written = await this.WriteNameToClusterAsync(
          new FaceCluster(0, mergedFaces), targetName
        );
      } catch (Exception ex) {
        this.SetStatus($"Merge write failed: {ex.Message}");
        return;
      }

      mergedFaces = mergedFaces
        .Select(f => new ScannedFace(f.File, f.Region with { Label = targetName, Status = RegionStatus.Accepted }))
        .ToList();
      this.UpdateScannedFaces(mergedFaces);
      this.CollapseIntoSingleCluster(selected, mergedFaces);
      this.SetStatus($"Merged into \"{targetName}\"; {written} photo(s) updated.");
    } else {
      // No name yet: collapse the VM list locally so the user sees the merge.
      // The real XMP merge happens when they name-and-apply the combined group.
      this.CollapseIntoSingleCluster(selected, mergedFaces);
      this.SetStatus($"Merged {selected.Count} clusters locally — enter a name and click Apply to persist.");
    }
  }

  /// <summary>
  /// Collapse the selected cluster VMs into one combined tile at the top of
  /// the gallery. Keeps one VM instead of many so merging is visible in the
  /// UI without a re-scan. Thumbnail will be (re-)generated for the combined
  /// cluster's representative on the next PopulateThumbnailsAsync pass.
  /// </summary>
  private void CollapseIntoSingleCluster(IReadOnlyList<FaceClusterViewModel> selected, IReadOnlyList<ScannedFace> mergedFaces) {
    var firstId = selected[0].Cluster.Id;
    var combined = new FaceCluster(firstId, mergedFaces);
    var mergedVm = new FaceClusterViewModel(combined);
    foreach (var c in selected)
      this._clusterViewModels.Remove(c);
    this._clusterViewModels.Insert(0, mergedVm);
    _ = this.PopulateThumbnailsAsync(new[] { mergedVm });
  }

  private async void OnClusterDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Control { Tag: FaceClusterViewModel vm })
      return;

    var detail = new FaceClusterDetailWindow(vm.Cluster, this._reader, this._writer);
    var result = await detail.ShowDialog<IReadOnlyList<ScannedFace>?>(this);
    if (result == null || result.Count == 0)
      return;

    this.UpdateScannedFaces(result);
    this.SplitOffUnmerged(vm, result);
    this.SetStatus($"Unmerged {result.Count} face(s) from \"{vm.DisplayName}\".");
  }

  /// <summary>
  /// Remove <paramref name="unmerged"/> members from <paramref name="source"/>'s
  /// cluster VM and append them as unnamed singleton VMs. Keeps the source
  /// VM's existing thumbnail unless its representative (first member) was
  /// one of the removed faces — in which case we clear the thumbnail so
  /// the next populate picks the new representative.
  /// </summary>
  private void SplitOffUnmerged(FaceClusterViewModel source, IReadOnlyList<ScannedFace> unmerged) {
    var removedKeys = unmerged
      .Select(f => (File: f.File.FullName, f.Region.Box))
      .ToHashSet();

    var remaining = source.Cluster.Members
      .Where(m => !removedKeys.Contains((m.File.FullName, m.Region.Box)))
      .ToList();

    var sourceIndex = this._clusterViewModels.IndexOf(source);
    if (sourceIndex < 0)
      return;

    if (remaining.Count == 0) {
      this._clusterViewModels.RemoveAt(sourceIndex);
    } else {
      var newCluster = new FaceCluster(source.Cluster.Id, remaining);
      var newVm = new FaceClusterViewModel(newCluster) { Name = source.Name };
      // Keep the existing thumbnail if the original representative is still
      // a member; otherwise clear it so the next populate picks a new face.
      var originalRepresentative = source.Cluster.Members.FirstOrDefault();
      if (originalRepresentative != null
          && remaining.Any(m => m.File.FullName.Equals(originalRepresentative.File.FullName, StringComparison.OrdinalIgnoreCase)
                              && m.Region.Box.Equals(originalRepresentative.Region.Box))) {
        newVm.Thumbnail = source.Thumbnail;
      }
      this._clusterViewModels[sourceIndex] = newVm;
    }

    // Unmerged faces come back with Label=null — surface them as singletons.
    var newSingletons = new List<FaceClusterViewModel>(unmerged.Count);
    var nextId = this._clusterViewModels.Count == 0
      ? 1
      : this._clusterViewModels.Max(c => c.Cluster.Id) + 1;
    foreach (var face in unmerged) {
      var vm = new FaceClusterViewModel(new FaceCluster(nextId++, new[] { face }));
      this._clusterViewModels.Add(vm);
      newSingletons.Add(vm);
    }

    _ = this.PopulateThumbnailsAsync(newSingletons);
  }

  /// <summary>
  /// Mirror a metadata write back into the cached scan so subsequent
  /// operations (merge, unmerge, search) see the updated labels without a
  /// full re-scan of the folder.
  /// </summary>
  private void UpdateScannedFaces(IReadOnlyList<ScannedFace> updated) {
    if (this._lastScan.Count == 0 || updated.Count == 0)
      return;
    var byKey = updated.ToDictionary(
      f => (f.File.FullName.ToLowerInvariant(), f.Region.Box),
      f => f
    );
    this._lastScan = this._lastScan
      .Select(f => byKey.TryGetValue((f.File.FullName.ToLowerInvariant(), f.Region.Box), out var replacement) ? replacement : f)
      .ToList();
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
