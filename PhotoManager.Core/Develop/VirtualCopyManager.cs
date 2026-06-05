namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Represents one virtual copy of a source image — a numbered XMP sidecar
/// that stores an independent set of develop settings without duplicating
/// the pixel data.
/// </summary>
/// <param name="SourceFile">The original image (e.g. IMG_001.jpg).</param>
/// <param name="SidecarFile">The sidecar on disk (e.g. IMG_001.copy2.xmp).</param>
/// <param name="CopyNumber">Positive integer (1, 2, …); 0 is the embedded/original.</param>
public sealed record VirtualCopyInfo(FileInfo SourceFile, FileInfo SidecarFile, int CopyNumber);

/// <summary>
/// Discovery + CRUD for virtual-copy XMP sidecars. Each virtual copy stores
/// its own <c>pm:developSettings</c> / <c>crs:</c> tags in a
/// <c>{stem}.copy{N}.xmp</c> file next to the source image. The original's
/// sidecar remains <c>{stem}.xmp</c> (or embedded XMP for JPEGs).
/// </summary>
public static class VirtualCopyManager {
  /// <summary>
  /// Scans the directory containing <paramref name="sourceFile"/> for
  /// <c>{stem}.copy{N}.xmp</c> siblings and returns them in ascending
  /// copy-number order.
  /// </summary>
  public static IReadOnlyList<VirtualCopyInfo> Discover(FileInfo sourceFile) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    var pairs = VirtualCopyDiscovery.Enumerate(sourceFile);
    var result = new List<VirtualCopyInfo>(pairs.Count);
    foreach (var (index, sidecar) in pairs)
      result.Add(new VirtualCopyInfo(sourceFile, sidecar, index));
    return result;
  }

  /// <summary>
  /// Creates the next available virtual copy for <paramref name="sourceFile"/>,
  /// writing an initial XMP sidecar with the given (or default) develop
  /// settings. Returns the <see cref="VirtualCopyInfo"/> describing the new
  /// copy.
  /// </summary>
  /// <param name="sourceFile">The original image file.</param>
  /// <param name="baseSettings">
  /// Optional settings to seed the copy with. When null, identity (default)
  /// settings are written so the copy starts from a clean slate.
  /// </param>
  public static VirtualCopyInfo CreateCopy(FileInfo sourceFile, DevelopSettings? baseSettings = null) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    if (!sourceFile.Exists)
      throw new FileNotFoundException("Source file does not exist.", sourceFile.FullName);

    var nextIndex = VirtualCopyDiscovery.NextAvailableIndex(sourceFile);
    var settings = baseSettings ?? new DevelopSettings();

    // Write the sidecar synchronously (blocking on the async save) so the
    // method stays simple for callers that don't need async.
    var ok = DevelopMetadataStore.SaveAsync(sourceFile, settings, nextIndex, snapshotLabel: null)
      .GetAwaiter().GetResult();
    if (!ok)
      throw new InvalidOperationException($"Failed to write sidecar for copy {nextIndex}.");

    var sidecar = VirtualCopyDiscovery.SidecarFor(sourceFile, nextIndex);
    return new VirtualCopyInfo(sourceFile, sidecar, nextIndex);
  }

  /// <summary>
  /// Deletes the sidecar file for the given virtual copy. Does nothing if
  /// the sidecar does not exist.
  /// </summary>
  public static void DeleteCopy(VirtualCopyInfo copy) {
    ArgumentNullException.ThrowIfNull(copy);
    if (copy.SidecarFile.Exists)
      copy.SidecarFile.Delete();
  }
}
