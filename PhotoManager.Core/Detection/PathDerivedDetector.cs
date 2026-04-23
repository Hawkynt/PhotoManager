using System.Text.RegularExpressions;

namespace PhotoManager.Core.Detection;

/// <summary>
/// A heuristic detector that extracts keyword-like tokens from the file's
/// directory path. Useful on its own for folder-organized libraries
/// ("Photos/2024/Vacation/Rome" → {vacation, rome}) and as a demoable default
/// until the ONNX-based detector (YOLO, RetinaFace) is wired up.
///
/// Tokens that look like dates, pure numbers, or common filler words are
/// filtered out; everything else becomes a low-confidence Object label.
/// </summary>
public sealed class PathDerivedDetector : IDetector {
  // Limit how far up the tree we walk. Going too deep picks up OS/user-profile
  // directory noise ("users", "appdata", "documents") on Windows/macOS.
  private const int MaxParentDepth = 4;

  private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) {
    "photos", "photo", "pictures", "pics", "images", "img", "dcim", "camera",
    "originals", "backup", "backups", "archive", "archived", "misc",
    "new folder", "raw", "jpg", "jpeg", "png",
    "users", "user", "home", "documents", "desktop",
    "appdata", "local", "roaming", "library", "caches",
    "temp", "tmp", "windows", "program files", "system32",
    "my pictures", "my documents",
    "c", "d", "e", "f", "g", "h"
  };

  private static readonly Regex DateLike = new(@"^\d{4}([\-_\.]?\d{1,2}([\-_\.]?\d{1,2})?)?$", RegexOptions.Compiled);
  private static readonly Regex PureNumber = new(@"^\d+$", RegexOptions.Compiled);
  private static readonly Regex DriveRoot = new(@"^[a-z]:$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

  public Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);

    var tokens = EnumeratePathTokens(imageFile)
      .Select(Sanitize)
      .Where(t => !string.IsNullOrWhiteSpace(t))
      .Where(t => !DriveRoot.IsMatch(t))
      .Where(t => !StopWords.Contains(t))
      .Where(t => !DateLike.IsMatch(t))
      .Where(t => !PureNumber.IsMatch(t))
      .Where(t => t.Length >= 2 && t.Length <= 40)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var labels = tokens
      .Select(t => new DetectionLabel(t, Confidence: 0.5f, DetectionKind.Object))
      .ToArray();

    return Task.FromResult(new DetectionResult(labels));
  }

  private static IEnumerable<string> EnumeratePathTokens(FileInfo file) {
    var dir = file.Directory;
    var depth = 0;
    while (dir != null && depth < MaxParentDepth) {
      // Skip the drive root (e.g. "D:\") — it has no Parent and its Name
      // is the drive letter itself, which is never semantically useful.
      if (dir.Parent == null)
        yield break;

      foreach (var token in SplitSegment(dir.Name))
        yield return token;
      dir = dir.Parent;
      depth++;
    }
  }

  private static IEnumerable<string> SplitSegment(string segment) {
    // Split on common path-word separators.
    return segment.Split(new[] { ' ', '_', '-', '.', '+' }, StringSplitOptions.RemoveEmptyEntries);
  }

  private static string Sanitize(string raw) {
    var trimmed = raw.Trim().Trim('(', ')', '[', ']', '{', '}', ',', ';', '!', '?');
    return trimmed.ToLowerInvariant();
  }
}
