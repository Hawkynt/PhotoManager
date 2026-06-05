namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One frame queued for the panorama stitcher. The window's grid binds to a
/// list of these so users can see which frames will be merged and in what
/// order — order matters in tripod mode (sequential pairwise alignment).
/// </summary>
public sealed class PanoramaInputRow {
  public PanoramaInputRow(FileInfo file, int width, int height) {
    this.File = file;
    this.FileName = file.Name;
    this.Dimensions = $"{width}x{height}";
  }

  public FileInfo File { get; }
  public string FileName { get; }
  public string Dimensions { get; }
}
