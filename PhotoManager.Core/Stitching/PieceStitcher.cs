using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Stitching;

/// <summary>
/// Result of a torn-paper reassembly run. <see cref="Canvas"/> is the
/// rendered reassembled image (RGBA, alpha 0 outside the union of placed
/// pieces); <see cref="Pieces"/> is everything the segmenter detected,
/// including those the assembler couldn't place. Caller owns the canvas
/// and should dispose it.
/// </summary>
public sealed class PieceStitchResult : IDisposable {
  public required Image<Rgba32> Canvas { get; init; }
  public required IReadOnlyList<DetectedPiece> Pieces { get; init; }
  public required IReadOnlyList<PieceAssembler.PlacedPiece> Placed { get; init; }
  public required IReadOnlyList<DetectedPiece> Unplaced { get; init; }

  public void Dispose() {
    this.Canvas.Dispose();
    foreach (var p in this.Pieces) p.Image.Dispose();
  }
}

/// <summary>
/// Top-level orchestrator. Wraps the three-phase pipeline (segment → match →
/// assemble) behind a single call so callers don't have to know the order.
/// Stable public API — UI and CLI bind to <see cref="Run"/> and
/// <see cref="PieceStitchResult"/>.
/// </summary>
public static class PieceStitcher {
  /// <summary>
  /// Run the full stitch pipeline against <paramref name="source"/>.
  /// </summary>
  public static PieceStitchResult Run(
      Image<Rgba32> source,
      ScannerBackground background = ScannerBackground.Auto,
      int minPieceArea = PieceSegmenter.DefaultMinPieceArea,
      double maxOverlapFraction = PieceAssembler.DefaultMaxOverlapFraction,
      double matchResidualCeiling = PieceAssembler.DefaultMatchResidualCeiling) {
    ArgumentNullException.ThrowIfNull(source);
    var pieces = PieceSegmenter.Segment(source, background, minPieceArea);
    var assembled = PieceAssembler.Assemble(pieces, maxOverlapFraction, matchResidualCeiling);
    return new PieceStitchResult {
      Canvas = assembled.Canvas,
      Pieces = pieces,
      Placed = assembled.Placed,
      Unplaced = assembled.Unplaced
    };
  }
}
