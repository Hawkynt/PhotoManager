namespace PhotoManager.Core.Models;

/// <summary>
/// Incremental progress reported during a model download.
/// <see cref="BytesReceived"/> always grows; <see cref="TotalBytes"/> is
/// null until the server responds with a Content-Length header.
/// </summary>
public readonly record struct ModelDownloadProgress(long BytesReceived, long? TotalBytes) {
  public double? Fraction =>
    this.TotalBytes is > 0 ? (double)this.BytesReceived / this.TotalBytes.Value : null;
}
