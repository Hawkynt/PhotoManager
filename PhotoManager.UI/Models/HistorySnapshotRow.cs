using System.Globalization;
using Avalonia.Media.Imaging;
using Hawkynt.PhotoManager.Core.Develop;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One row in the EditHistoryWindow snapshot list. Wraps a
/// <see cref="DevelopSnapshot"/> with display-friendly strings and a
/// pre-rendered thumbnail so the XAML stays binding-only.
/// </summary>
public sealed class HistorySnapshotRow {
  public HistorySnapshotRow(int index, DevelopSnapshot snapshot, Bitmap? thumbnail) {
    this.Index = index;
    this.Snapshot = snapshot;
    this.Thumbnail = thumbnail;
  }

  public int Index { get; }

  public DevelopSnapshot Snapshot { get; }

  public Bitmap? Thumbnail { get; }

  public string TimestampText
    => this.Snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

  public string LabelText
    => string.IsNullOrWhiteSpace(this.Snapshot.Label) ? "(no label)" : this.Snapshot.Label!;
}
