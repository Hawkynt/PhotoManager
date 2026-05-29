using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Previews;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Extended memories browser. Surfaces photos across five time windows
/// (Day / Week / Month / Year / Decade) powered by <see cref="MemoriesFinder"/>,
/// plus the legacy "On this trip" powered by <see cref="MemoriesFilter"/>.
/// Filter buttons at the top let the user narrow to one window or show all.
/// Double-clicking a tile opens the photo in <see cref="EditImageWindow"/>.
/// </summary>
public partial class MemoriesWindow : Window {
  private readonly List<MemoryRowViewModel> _allViewModels = new();
  private CancellationTokenSource? _thumbCts;

  // The computed groups from MemoriesFinder + OnThisTrip, stored so filter
  // toggles can rebuild the panel without recomputing.
  private IReadOnlyList<MemoryGroup> _finderGroups = Array.Empty<MemoryGroup>();
  private List<MemoryRowViewModel> _tripViewModels = new();
  private string _tripHeader = string.Empty;
  private string _activeFilter = "All";

  public MemoriesWindow() : this(Array.Empty<(FileInfo, FullMetadata)>(), null) { }

  public MemoriesWindow(IReadOnlyList<(FileInfo File, FullMetadata Metadata)> photos, (FileInfo File, FullMetadata Metadata)? anchor) {
    this.InitializeComponent();

    this.ComputeGroups(photos, anchor);
    this.RebuildPanel();

    this.Opened += (_, _) => {
      this._thumbCts = new CancellationTokenSource();
      _ = this.LoadThumbnailsAsync(this._thumbCts.Token);
    };
    this.Closed += (_, _) => this._thumbCts?.Cancel();
  }

  private void ComputeGroups(IReadOnlyList<(FileInfo File, FullMetadata Metadata)> photos, (FileInfo File, FullMetadata Metadata)? anchor) {
    var today = DateTime.Now;

    // Build the (capturedDate, file) pairs for MemoriesFinder
    var datedPhotos = photos
      .Where(p => p.Metadata.DateCreated.HasValue)
      .Select(p => (p.Metadata.DateCreated!.Value, p.File))
      .ToList();

    this._finderGroups = MemoriesFinder.Find(datedPhotos, today);

    // Build "On this trip" group using the legacy MemoriesFilter
    this._tripViewModels = new List<MemoryRowViewModel>();
    if (anchor is { } a && a.Metadata.Gps is { } gps && a.Metadata.DateCreated is { } captured) {
      var matches = MemoriesFilter.OnThisTrip(photos, gps, captured, radiusKm: 5, window: TimeSpan.FromDays(3))
        .OrderBy(p => p.Metadata.DateCreated ?? DateTime.MinValue)
        .ToList();
      foreach (var (file, md) in matches)
        this._tripViewModels.Add(new MemoryRowViewModel(file, md));
      this._tripHeader = matches.Count == 0
        ? $"No nearby photos within 5 km / ±3 days of {a.File.Name}."
        : $"📍 On this trip — {matches.Count} photo(s) within 5 km of {a.File.Name} ({captured:yyyy-MM-dd})";
    } else if (anchor is { } a2) {
      this._tripHeader = $"{a2.File.Name} has no GPS or capture date — can't compute trip neighbours.";
    } else {
      this._tripHeader = "Select a photo with GPS in the main grid first to see nearby photos.";
    }

    // Build MemoryRowViewModels for finder groups. We need metadata to show
    // location/date on tiles, so build a file->metadata lookup.
    var metaLookup = new Dictionary<string, FullMetadata>(StringComparer.OrdinalIgnoreCase);
    foreach (var (file, md) in photos)
      metaLookup.TryAdd(file.FullName, md);

    // Map each MemoryGroup's FileInfo list into MemoryRowViewModels
    foreach (var group in this._finderGroups) {
      // Attach ViewModels to the group via a side-channel dictionary
      // so the panel builder can access them.
    }

    // Pre-build all VMs and store them keyed by group reference
    this._groupViewModels.Clear();
    foreach (var group in this._finderGroups) {
      var vms = new List<MemoryRowViewModel>();
      foreach (var file in group.Photos) {
        if (metaLookup.TryGetValue(file.FullName, out var md))
          vms.Add(new MemoryRowViewModel(file, md));
        else
          vms.Add(new MemoryRowViewModel(file, new FullMetadata()));
      }
      this._groupViewModels[group] = vms;
    }
  }

  private readonly Dictionary<MemoryGroup, List<MemoryRowViewModel>> _groupViewModels = new();

  /// <summary>
  /// Rebuild the visual panel from the computed groups, respecting the active
  /// filter. Called on initial load and whenever a filter toggle changes.
  /// </summary>
  private void RebuildPanel() {
    var panel = this.FindControl<StackPanel>("GroupsPanel");
    if (panel is null)
      return;

    panel.Children.Clear();
    this._allViewModels.Clear();

    var totalPhotos = 0;
    var totalGroups = 0;

    // Finder groups (Day/Week/Month/Year/Decade)
    foreach (var group in this._finderGroups) {
      if (!ShouldShowGroup(group.TimeWindow, this._activeFilter))
        continue;

      if (!this._groupViewModels.TryGetValue(group, out var vms) || vms.Count == 0)
        continue;

      this.AddGroupToPanel(panel, $"📅 {group.Header}", vms);
      this._allViewModels.AddRange(vms);
      totalPhotos += vms.Count;
      totalGroups++;
    }

    // Trip group
    if (this._activeFilter is "All" or "Trip" && this._tripViewModels.Count > 0) {
      this.AddGroupToPanel(panel, this._tripHeader, this._tripViewModels);
      this._allViewModels.AddRange(this._tripViewModels);
      totalPhotos += this._tripViewModels.Count;
      totalGroups++;
    } else if (this._activeFilter == "Trip" && this._tripViewModels.Count == 0) {
      var hint = new TextBlock {
        Text = this._tripHeader,
        Opacity = 0.6,
        Margin = new Avalonia.Thickness(4)
      };
      panel.Children.Add(hint);
    }

    // Empty state
    var emptyState = this.FindControl<StackPanel>("EmptyState");
    if (emptyState is not null)
      emptyState.IsVisible = totalGroups == 0 && this._activeFilter != "Trip";

    // Summary
    if (this.FindControl<TextBlock>("SummaryText") is { } summary)
      summary.Text = totalGroups == 0
        ? string.Empty
        : $"{totalPhotos} photo(s) in {totalGroups} group(s)";

    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = $"Reference: {DateTime.Now:yyyy-MM-dd}";
  }

  private void AddGroupToPanel(StackPanel panel, string header, List<MemoryRowViewModel> vms) {
    var headerBlock = new TextBlock {
      Text = header,
      FontWeight = Avalonia.Media.FontWeight.SemiBold,
      FontSize = 14,
      Margin = new Avalonia.Thickness(4, 8, 4, 4)
    };
    panel.Children.Add(headerBlock);

    var wrap = new WrapPanel {
      Orientation = Avalonia.Layout.Orientation.Horizontal
    };

    foreach (var vm in vms) {
      var tile = BuildTile(vm);
      wrap.Children.Add(tile);
    }

    panel.Children.Add(wrap);
  }

  private Border BuildTile(MemoryRowViewModel vm) {
    var image = new Image {
      [!Image.SourceProperty] = new Avalonia.Data.Binding("Thumbnail") { Source = vm },
      Stretch = Avalonia.Media.Stretch.Uniform
    };
    var imageBorder = new Border {
      Height = 140,
      Child = image
    };
    // Apply ThumbBackgroundBrush via dynamic resource
    imageBorder.Bind(Border.BackgroundProperty,
      imageBorder.GetResourceObservable("ThumbBackgroundBrush"));

    var fileName = new TextBlock {
      Text = vm.FileName,
      FontWeight = Avalonia.Media.FontWeight.SemiBold,
      FontSize = 11,
      TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
    };
    ToolTip.SetTip(fileName, vm.FileName);

    var dateText = new TextBlock {
      Text = vm.DateText,
      FontSize = 11,
      Opacity = 0.75
    };

    var locationText = new TextBlock {
      Text = vm.LocationText,
      FontSize = 11,
      Opacity = 0.6,
      TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
    };

    var infoPanel = new StackPanel {
      Spacing = 2,
      Margin = new Avalonia.Thickness(6),
      Children = { fileName, dateText, locationText }
    };

    var content = new StackPanel {
      Children = { imageBorder, infoPanel }
    };

    var tile = new Border {
      Width = 200,
      Margin = new Avalonia.Thickness(4),
      BorderThickness = new Avalonia.Thickness(1),
      CornerRadius = new Avalonia.CornerRadius(4),
      Child = content,
      DataContext = vm
    };
    tile.Bind(Border.BorderBrushProperty,
      tile.GetResourceObservable("PanelBorderBrush"));

    tile.DoubleTapped += this.OnTileDoubleTapped;

    return tile;
  }

  private static bool ShouldShowGroup(TimeWindow window, string filter) => filter switch {
    "All" => true,
    "Day" => window == TimeWindow.Day,
    "Week" => window == TimeWindow.Week,
    "Month" => window == TimeWindow.Month,
    "Year" => window == TimeWindow.Year,
    "Decade" => window == TimeWindow.Decade,
    _ => false
  };

  private void OnFilterClick(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton clicked || clicked.Tag is not string tag)
      return;

    // Radio behavior: uncheck all others, keep clicked checked
    foreach (var name in new[] { "FilterAll", "FilterDay", "FilterWeek", "FilterMonth", "FilterYear", "FilterDecade", "FilterTrip" }) {
      if (this.FindControl<ToggleButton>(name) is { } tb && !ReferenceEquals(tb, clicked))
        tb.IsChecked = false;
    }
    clicked.IsChecked = true;
    this._activeFilter = tag;

    this.RebuildPanel();

    // Reload thumbnails for newly visible tiles
    this._thumbCts?.Cancel();
    this._thumbCts = new CancellationTokenSource();
    _ = this.LoadThumbnailsAsync(this._thumbCts.Token);
  }

  private async Task LoadThumbnailsAsync(CancellationToken token) {
    var fullFrame = new NormalizedBoundingBox(0, 0, 1, 1);
    foreach (var vm in this._allViewModels.ToList()) {
      if (token.IsCancellationRequested)
        return;
      if (vm.Thumbnail is not null)
        continue;
      try {
        var bytes = await RegionThumbnailExtractor.CropAsync(vm.File, fullFrame, token);
        if (bytes is null)
          continue;
        await Dispatcher.UIThread.InvokeAsync(() => {
          using var ms = new MemoryStream(bytes, writable: false);
          vm.Thumbnail = new Bitmap(ms);
        });
      } catch (OperationCanceledException) {
        return;
      } catch {
        // Skip failed thumbnails — tile renders blank placeholder.
      }
    }
  }

  private void OnTileDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Border { DataContext: MemoryRowViewModel vm })
      return;
    try {
      var window = new EditImageWindow(vm.File);
      window.Show();
    } catch {
      // Fall back to OS default viewer
      try {
        ShellLauncher.OpenInDefaultViewer(vm.File.FullName);
      } catch {
        // Best-effort; failure is silent.
      }
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();
}
