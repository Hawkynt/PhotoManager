namespace PhotoManager.Core.Models;

/// <summary>
/// A downloadable ONNX model plus where it lives on disk after download.
/// <see cref="FileName"/> must match the filename the consuming detector
/// expects (e.g. <c>yolov8n.onnx</c> or <c>face-detector.onnx</c>) so a
/// successful download is immediately usable.
/// </summary>
public sealed record ModelInfo(
  string Name,
  string FileName,
  string DisplayName,
  string Description,
  string DownloadUrl,
  long ApproximateSizeBytes
) {
  /// <summary>Resolves where the model will land after download.</summary>
  public FileInfo ResolveDestination() => AppDataPaths.ModelFile(this.FileName);

  public bool IsInstalled() {
    var path = this.ResolveDestination();
    return path.Exists && path.Length > 0;
  }
}
