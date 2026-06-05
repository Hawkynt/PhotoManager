using System.Globalization;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// Row VM for the QualityScanWindow grid. Strings are pre-formatted so the
/// DataGrid columns can bind directly without a converter.
/// </summary>
public sealed class QualityScanRow {
  public QualityScanRow(FileInfo file, QualityResult result) {
    this.File = file;
    this.Result = result;
    this.FileName = file.Name;
    this.Folder = file.DirectoryName ?? "";
    this.SharpnessText = result.SharpnessScore.ToString("0.0", CultureInfo.InvariantCulture);
    this.BlurryText = result.IsBlurry ? "✓ blurry" : "";
    this.OverexposedText = result.IsOverexposed
      ? string.Create(CultureInfo.InvariantCulture, $"✓ {result.ClippedHighlightFraction * 100:0.0}%")
      : "";
    this.UnderexposedText = result.IsUnderexposed
      ? string.Create(CultureInfo.InvariantCulture, $"✓ {result.ClippedShadowFraction * 100:0.0}%")
      : "";
  }

  public FileInfo File { get; }
  public QualityResult Result { get; }
  public string FileName { get; }
  public string Folder { get; }
  public string SharpnessText { get; }
  public string BlurryText { get; }
  public string OverexposedText { get; }
  public string UnderexposedText { get; }
  public bool IsAnyFlag => this.Result.IsBlurry || this.Result.IsOverexposed || this.Result.IsUnderexposed;
}
