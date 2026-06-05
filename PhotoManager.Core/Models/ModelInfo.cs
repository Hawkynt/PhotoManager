namespace Hawkynt.PhotoManager.Core.Models;

/// <summary>
/// A downloadable ONNX model plus where it lives on disk after download.
/// <see cref="FileName"/> must match the filename the consuming detector
/// expects (e.g. <c>yolov8n.onnx</c> or <c>face-detector.onnx</c>) so a
/// successful download is immediately usable.
///
/// Some ONNX exports (DeOldify, BLIP-2, Florence-2 mini) split the graph
/// from the weights — the .onnx file references a sibling .onnx.data file
/// that ONNX Runtime resolves at session-open time. Those models declare
/// their companion files in <see cref="ExternalDataFiles"/>; the downloader
/// fetches them all and <see cref="IsInstalled"/> requires every file to
/// be present before reporting available.
/// </summary>
public sealed record ModelInfo(
  string Name,
  string FileName,
  string DisplayName,
  string Description,
  string DownloadUrl,
  long ApproximateSizeBytes,
  IReadOnlyList<ExternalDataFile>? ExternalDataFiles = null
) {
  /// <summary>Resolves where the model will land after download.</summary>
  public FileInfo ResolveDestination() => AppDataPaths.ModelFile(this.FileName);

  /// <summary>Total bytes downloaded across primary + every external data file. Used by the download dialog progress.</summary>
  public long TotalDownloadBytes {
    get {
      var total = this.ApproximateSizeBytes;
      if (this.ExternalDataFiles is { Count: > 0 } files)
        foreach (var f in files)
          total += f.ApproximateSizeBytes;
      return total;
    }
  }

  public bool IsInstalled() {
    var path = this.ResolveDestination();
    if (!path.Exists || path.Length <= 0)
      return false;
    if (this.ExternalDataFiles is not { Count: > 0 } files)
      return true;
    foreach (var f in files) {
      var fp = AppDataPaths.ModelFile(f.FileName);
      if (!fp.Exists || fp.Length <= 0)
        return false;
    }
    return true;
  }
}

/// <summary>
/// Companion data file for a multi-file ONNX export (e.g. DeOldify's
/// 244 MB <c>deoldify-artistic.onnx.data</c> referenced by the 423 KB
/// <c>deoldify-artistic.onnx</c> graph). Lives next to the primary file
/// in the models directory.
/// </summary>
public sealed record ExternalDataFile(
  string FileName,
  string DownloadUrl,
  long ApproximateSizeBytes
);
