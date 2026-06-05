using System.Net.Http;

namespace Hawkynt.PhotoManager.Core.Models;

/// <summary>
/// Downloads an ONNX model to the app-data models directory. Writes to a
/// temp file and renames on completion so a half-finished download never
/// leaves a corrupt model in place; errors and cancellations clean up.
/// Streams the response body and reports incremental progress so the UI
/// can show a real bar for the multi-megabyte YOLO weights.
/// </summary>
public sealed class ModelDownloader : IDisposable {
  private const string DefaultUserAgent = "PhotoManager/1.0 (https://github.com/Hawkynt/PhotoManager)";

  private readonly HttpClient _httpClient;
  private readonly bool _ownsHttpClient;

  public ModelDownloader(HttpClient? httpClient = null, string userAgent = DefaultUserAgent) {
    this._ownsHttpClient = httpClient == null;
    this._httpClient = httpClient ?? new HttpClient {
      // Large models need a generous timeout even on a moderate connection.
      Timeout = TimeSpan.FromMinutes(5)
    };

    if (!this._httpClient.DefaultRequestHeaders.UserAgent.Any())
      this._httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
  }

  /// <summary>
  /// Downloads the given <paramref name="model"/> to its resolved destination
  /// under the app-data models directory. For multi-file ONNX exports
  /// (<see cref="ModelInfo.ExternalDataFiles"/> non-empty) the primary
  /// graph file is fetched first, then each companion data file in order.
  /// Progress is reported as cumulative bytes across all files so the
  /// UI bar advances smoothly through the whole sequence.
  /// </summary>
  public async Task<FileInfo> DownloadAsync(
    ModelInfo model,
    IProgress<ModelDownloadProgress>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(model);

    var totalAcrossAllFiles = model.TotalDownloadBytes;
    long bytesBefore = 0;

    var cumulative = progress is null ? null : new Progress<ModelDownloadProgress>(p => {
      progress.Report(new ModelDownloadProgress(bytesBefore + p.BytesReceived, totalAcrossAllFiles));
    });

    // Primary graph file.
    var primary = await this.DownloadAsync(model.DownloadUrl, model.ResolveDestination(), cumulative, cancellationToken);
    bytesBefore += model.ApproximateSizeBytes;

    // Companion data files (DeOldify-style external weights). ONNX Runtime
    // resolves these by looking next to the .onnx file at session-open time,
    // so the destination directory must match the primary's.
    if (model.ExternalDataFiles is { Count: > 0 } files)
      foreach (var dataFile in files) {
        cancellationToken.ThrowIfCancellationRequested();
        var dest = AppDataPaths.ModelFile(dataFile.FileName);
        await this.DownloadAsync(dataFile.DownloadUrl, dest, cumulative, cancellationToken);
        bytesBefore += dataFile.ApproximateSizeBytes;
      }

    return primary;
  }

  /// <summary>
  /// Downloads <paramref name="url"/> into <paramref name="destination"/>,
  /// writing via a <c>.part</c> temp file and atomically swapping on
  /// completion. Exposed so tests (and any future offline-mode tooling) can
  /// target an arbitrary path without routing through <see cref="AppDataPaths"/>.
  /// </summary>
  public async Task<FileInfo> DownloadAsync(
    string url,
    FileInfo destination,
    IProgress<ModelDownloadProgress>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(url);
    ArgumentNullException.ThrowIfNull(destination);

    destination.Directory?.Create();

    var tempPath = destination.FullName + ".part";
    try {
      using var response = await this._httpClient.GetAsync(
        url,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      );
      response.EnsureSuccessStatusCode();

      var totalBytes = response.Content.Headers.ContentLength;

      await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81_920, useAsync: true))
      await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)) {
        var buffer = new byte[81_920];
        long received = 0;

        while (true) {
          var read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
          if (read <= 0)
            break;

          await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
          received += read;
          progress?.Report(new ModelDownloadProgress(received, totalBytes));
        }
      }

      // LFS-pointer sniff is always safe — they're always tiny ASCII text.
      DetectLfsPointer(tempPath);

      if (File.Exists(destination.FullName))
        File.Replace(tempPath, destination.FullName, destinationBackupFileName: null);
      else
        File.Move(tempPath, destination.FullName);

      destination.Refresh();
      return destination;
    } catch {
      SafeDelete(tempPath);
      throw;
    }
  }

  /// <summary>
  /// Catches the most common "200 OK but not what I wanted" case: GitHub
  /// returns the Git-LFS pointer text (<c>version https://git-lfs.github.com/spec/v1...</c>)
  /// instead of the binary when the actual LFS content isn't available. ONNX
  /// files never start with those ASCII bytes, so the check has no false positives.
  /// </summary>
  private static void DetectLfsPointer(string path) {
    var info = new FileInfo(path);
    if (info.Length < 40)
      return;  // too short to even hold the LFS pointer header

    using var stream = info.OpenRead();
    Span<byte> head = stackalloc byte[64];
    var read = stream.Read(head);
    var text = System.Text.Encoding.ASCII.GetString(head[..read]);
    if (text.StartsWith("version https://git-lfs", StringComparison.Ordinal))
      throw new InvalidDataException(
        "Download returned a Git-LFS pointer instead of the real file. "
        + "Try 'Install from file...' and point to a manually-downloaded ONNX.");
  }

  private static void SafeDelete(string path) {
    try {
      if (File.Exists(path))
        File.Delete(path);
    } catch {
      // Best-effort cleanup — not fatal if the file can't be removed.
    }
  }

  public void Dispose() {
    if (this._ownsHttpClient)
      this._httpClient.Dispose();
  }
}
