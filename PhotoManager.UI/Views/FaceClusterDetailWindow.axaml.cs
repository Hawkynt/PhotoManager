using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Faces;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Previews;
using Hawkynt.PhotoManager.Core.Regions;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Detail view for a single face cluster: shows every member as its own
/// thumbnail so the user can audit an auto-grouped cluster and split out
/// any faces that don't actually belong. Ticking members and clicking
/// "Unmerge selected" clears the cluster's name on those specific regions
/// via <see cref="CompositeMetadataWriter"/>, which drops them back into
/// their own singletons when the gallery refreshes. Closes returning the
/// list of unmerged faces (with Label cleared) so the parent can rebuild
/// its cluster list without a full rescan.
/// </summary>
public partial class FaceClusterDetailWindow : Window {
  private readonly FaceCluster _cluster;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;
  private readonly ObservableCollection<FaceMemberThumbnailViewModel> _members = new();

  public FaceClusterDetailWindow() {
    this.InitializeComponent();
    // Default ctor for the XAML designer; callers use the overload below.
    this._cluster = new FaceCluster(0, Array.Empty<ScannedFace>());
    this._reader = null!;
    this._writer = null!;
  }

  public FaceClusterDetailWindow(FaceCluster cluster, IMetadataReader reader, IMetadataWriter writer) {
    this.InitializeComponent();
    this._cluster = cluster;
    this._reader = reader;
    this._writer = writer;

    if (this.FindControl<ItemsControl>("MembersList") is { } list)
      list.ItemsSource = this._members;
    if (this.FindControl<TextBlock>("HeaderText") is { } header)
      header.Text = $"{cluster.DisplayName} — {cluster.Count} face(s)";

    foreach (var face in cluster.Members)
      this._members.Add(new FaceMemberThumbnailViewModel(face));

    _ = this.PopulateThumbnailsAsync();
  }

  private async Task PopulateThumbnailsAsync() {
    foreach (var vm in this._members.ToList()) {
      byte[]? bytes;
      try {
        bytes = await RegionThumbnailExtractor.CropAsync(vm.Face.File, vm.Face.Region.Box);
      } catch {
        bytes = null;
      }
      if (bytes == null)
        continue;

      try {
        var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
        Dispatcher.UIThread.Post(() => vm.Thumbnail = bitmap);
      } catch {
        // Skip — a missing thumbnail doesn't break the unmerge workflow.
      }
    }
  }

  private async void OnUnmergeClick(object? sender, RoutedEventArgs e) {
    var selected = this._members.Where(m => m.IsSelected).ToList();
    if (selected.Count == 0) {
      this.SetStatus("Tick at least one face to unmerge.");
      return;
    }
    if (selected.Count == this._members.Count) {
      this.SetStatus("You can't unmerge every face — that would leave the cluster empty. Deselect at least one.");
      return;
    }

    this.SetStatus($"Clearing name from {selected.Count} face(s)...");
    IReadOnlyList<ScannedFace> unmerged;
    try {
      unmerged = await this.ClearNameAsync(selected.Select(s => s.Face).ToList());
    } catch (Exception ex) {
      this.SetStatus($"Unmerge failed: {ex.Message}");
      return;
    }

    this.Close(unmerged);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close(null);

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }

  /// <summary>
  /// Clear the Label on the given regions via the metadata writer pipeline
  /// and return the resulting faces (Label=null). Keywords are intentionally
  /// left alone — another region in the same file may still carry that
  /// name, and stripping a shared keyword could discard valid tags.
  /// </summary>
  private async Task<IReadOnlyList<ScannedFace>> ClearNameAsync(IReadOnlyList<ScannedFace> faces) {
    var byFile = faces.GroupBy(f => f.File.FullName, StringComparer.OrdinalIgnoreCase);
    var result = new List<ScannedFace>(faces.Count);

    foreach (var group in byFile) {
      var file = new FileInfo(group.Key);
      if (!file.Exists)
        continue;

      var current = await this._reader.ReadAsync(file);
      var targetBoxes = group.Select(g => g.Region.Box).ToHashSet();

      var updatedRegions = current.Regions
        .Select(r => targetBoxes.Contains(r.Box) && r.Category == RegionCategory.Person
          ? r with { Label = null }
          : r)
        .ToList();

      var patch = new MetadataEdit {
        Regions = updatedRegions
      };
      await this._writer.ApplyAsync(file, patch);

      // Mirror the cleared label onto the returned faces so the caller can
      // plug them straight into the gallery as unnamed singletons.
      foreach (var face in group)
        result.Add(new ScannedFace(face.File, face.Region with { Label = null }));
    }

    return result;
  }
}
