using System.Globalization;
using System.Text.RegularExpressions;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Resolves the on-disk locations of "virtual copy" XMP sidecars next to a
/// source image. Copy 0 is the embedded XMP packet inside the source file
/// (no sidecar). Copies 1, 2, … live as <c>basename.copyN.xmp</c> alongside
/// the source so the original pixels are never duplicated. The numbering is
/// sparse-tolerant: copies 1 and 3 can exist without copy 2.
/// </summary>
public static class VirtualCopyDiscovery {
  // basename.copy<N>.xmp — case-insensitive, non-greedy basename match.
  private static readonly Regex CopyPattern =
    new(@"^(?<base>.+)\.copy(?<n>\d+)\.xmp$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

  /// <summary>
  /// Returns the copy-sidecar paths next to <paramref name="sourceFile"/> in
  /// ascending index order. Copy 0 (embedded XMP) is NOT returned — only
  /// stand-alone sidecars on disk.
  /// </summary>
  public static IReadOnlyList<(int Index, FileInfo Sidecar)> Enumerate(FileInfo sourceFile) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    var directory = sourceFile.Directory;
    if (directory is null || !directory.Exists)
      return Array.Empty<(int, FileInfo)>();

    var basename = Path.GetFileNameWithoutExtension(sourceFile.Name);
    if (string.IsNullOrEmpty(basename))
      return Array.Empty<(int, FileInfo)>();

    var prefix = basename + ".copy";
    var results = new List<(int Index, FileInfo Sidecar)>();
    foreach (var candidate in directory.EnumerateFiles(prefix + "*.xmp")) {
      var match = CopyPattern.Match(candidate.Name);
      if (!match.Success)
        continue;
      if (!string.Equals(match.Groups["base"].Value, basename, StringComparison.OrdinalIgnoreCase))
        continue;
      if (!int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        continue;
      if (n < 1)
        continue;
      results.Add((n, candidate));
    }
    results.Sort(static (a, b) => a.Index.CompareTo(b.Index));
    return results;
  }

  /// <summary>The copy indices currently present on disk (sorted ascending).</summary>
  public static IReadOnlyList<int> EnumerateIndices(FileInfo sourceFile)
    => Enumerate(sourceFile).Select(p => p.Index).ToArray();

  /// <summary>
  /// Smallest positive integer N such that <c>basename.copyN.xmp</c> is not
  /// already present. Gap-tolerant: with copies {1, 3} the next index is 2.
  /// </summary>
  public static int NextAvailableIndex(FileInfo sourceFile) {
    var taken = new HashSet<int>(EnumerateIndices(sourceFile));
    var n = 1;
    while (taken.Contains(n))
      n++;
    return n;
  }

  /// <summary>
  /// The on-disk sidecar path for copy <paramref name="copyIndex"/> next to
  /// <paramref name="sourceFile"/>. Throws when <paramref name="copyIndex"/>
  /// is &lt; 1 — copy 0 is the embedded packet and has no sidecar.
  /// </summary>
  public static FileInfo SidecarFor(FileInfo sourceFile, int copyIndex) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    if (copyIndex < 1)
      throw new ArgumentOutOfRangeException(nameof(copyIndex), copyIndex, "Copy 0 has no sidecar (embedded XMP).");
    var directory = sourceFile.Directory ?? throw new InvalidOperationException("Source file has no parent directory.");
    var basename = Path.GetFileNameWithoutExtension(sourceFile.Name);
    return new FileInfo(Path.Combine(directory.FullName, $"{basename}.copy{copyIndex.ToString(CultureInfo.InvariantCulture)}.xmp"));
  }
}
