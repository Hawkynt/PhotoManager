using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Represents one healing-brush stroke for undo/redo. Records the source
/// offset (deltaX, deltaY) and a list of (dstX, dstY, radius) stamps.
/// <see cref="Apply"/> replays all stamps onto an image.
/// <see cref="CaptureUndo"/> snapshots the destination pixels before the
/// stroke so undo can restore them.
/// </summary>
public sealed class HealingBrushStroke {
  /// <summary>Single stamp within a stroke.</summary>
  public readonly record struct Stamp(int DstX, int DstY, int Radius);

  /// <summary>
  /// Fixed offset from destination to source. Source = Dst + Delta.
  /// Set once when the user alt-clicks the source point and stays
  /// constant for the entire stroke (and across strokes until the
  /// user sets a new source).
  /// </summary>
  public int DeltaX { get; }
  public int DeltaY { get; }

  private readonly List<Stamp> _stamps = new();

  /// <summary>
  /// Undo snapshot: the bounding rectangle and original pixel data
  /// captured before the stroke was applied. Null until
  /// <see cref="CaptureUndo"/> is called.
  /// </summary>
  private Rectangle _undoBounds;
  private Rgba32[]? _undoPixels;

  public HealingBrushStroke(int deltaX, int deltaY) {
    this.DeltaX = deltaX;
    this.DeltaY = deltaY;
  }

  /// <summary>The stamps that make up this stroke (read-only view).</summary>
  public IReadOnlyList<Stamp> Stamps => this._stamps;

  /// <summary>Record a stamp at the given destination position and radius.</summary>
  public void AddStamp(int dstX, int dstY, int radius) {
    this._stamps.Add(new Stamp(dstX, dstY, radius));
  }

  /// <summary>
  /// Snapshot the destination pixels before applying the stroke.
  /// Captures a bounding rectangle that covers all stamps so undo
  /// can restore the region in one block.
  /// </summary>
  public void CaptureUndo(Image<Rgba32> image) {
    if (this._stamps.Count == 0)
      return;

    var minX = int.MaxValue;
    var minY = int.MaxValue;
    var maxX = int.MinValue;
    var maxY = int.MinValue;
    foreach (var s in this._stamps) {
      minX = Math.Min(minX, s.DstX - s.Radius);
      minY = Math.Min(minY, s.DstY - s.Radius);
      maxX = Math.Max(maxX, s.DstX + s.Radius);
      maxY = Math.Max(maxY, s.DstY + s.Radius);
    }
    minX = Math.Max(0, minX);
    minY = Math.Max(0, minY);
    maxX = Math.Min(image.Width - 1, maxX);
    maxY = Math.Min(image.Height - 1, maxY);

    if (minX > maxX || minY > maxY)
      return;

    var bw = maxX - minX + 1;
    var bh = maxY - minY + 1;
    this._undoBounds = new Rectangle(minX, minY, bw, bh);
    this._undoPixels = new Rgba32[bw * bh];

    image.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var row = accessor.GetRowSpan(minY + ly);
        var off = ly * bw;
        for (var lx = 0; lx < bw; lx++)
          this._undoPixels[off + lx] = row[minX + lx];
      }
    });
  }

  /// <summary>Replay all stamps onto the image, copying from source to destination.</summary>
  public void Apply(Image<Rgba32> image) {
    foreach (var s in this._stamps)
      HealingBrush.Apply(image, s.DstX + this.DeltaX, s.DstY + this.DeltaY, s.DstX, s.DstY, s.Radius);
  }

  /// <summary>Restore the pixels that were overwritten by this stroke.</summary>
  public void Undo(Image<Rgba32> image) {
    if (this._undoPixels is null)
      return;

    var b = this._undoBounds;
    var bw = b.Width;
    var bh = b.Height;
    image.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var ty = b.Y + ly;
        if (ty < 0 || ty >= accessor.Height) continue;
        var row = accessor.GetRowSpan(ty);
        var off = ly * bw;
        var startX = Math.Max(0, b.X);
        var endX = Math.Min(row.Length, b.X + bw);
        for (var tx = startX; tx < endX; tx++)
          row[tx] = this._undoPixels[off + (tx - b.X)];
      }
    });
  }
}
