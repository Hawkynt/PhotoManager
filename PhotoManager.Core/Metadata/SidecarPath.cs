namespace PhotoManager.Core.Metadata;

/// <summary>
/// Resolves the sidecar file location for a given image. We use the
/// darktable convention: <c>photo.jpg.xmp</c> rather than Adobe's
/// <c>photo.xmp</c>. This keeps sidecars unique when RAW + JPG pairs
/// live in the same directory.
/// </summary>
public static class SidecarPath {
  public const string Suffix = ".xmp";

  public static FileInfo For(FileInfo imageFile)
    => new(imageFile.FullName + Suffix);
}
