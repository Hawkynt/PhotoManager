using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hawkynt.PhotoManager.Core.Video;

/// <summary>
/// Cross-platform discovery of an installed ffmpeg binary. Tries the
/// system PATH first via which/where, then falls back to a curated list
/// of common install locations on each OS.
/// </summary>
public static class FfmpegLocator {
  private static readonly string ExeName =
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

  public static bool IsAvailable() => Find() is { Exists: true };

  public static FileInfo? Find() {
    var fromPath = FindOnPath();
    if (fromPath is { Exists: true })
      return fromPath;

    foreach (var candidate in CommonInstallPaths()) {
      var fi = new FileInfo(candidate);
      if (fi.Exists)
        return fi;
    }

    return null;
  }

  internal static FileInfo? FindOnPath() {
    // Walk PATH ourselves rather than shelling out — avoids spawning a
    // process when the binary is already in our environment, and keeps
    // the lookup unit-testable by setting PATH on a process.
    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(path))
      return null;

    var separator = Path.PathSeparator;
    foreach (var dir in path.Split(separator, StringSplitOptions.RemoveEmptyEntries)) {
      var trimmed = dir.Trim().Trim('"');
      if (trimmed.Length == 0)
        continue;
      string candidate;
      try {
        candidate = Path.Combine(trimmed, ExeName);
      } catch {
        continue;
      }
      if (File.Exists(candidate))
        return new FileInfo(candidate);
    }

    return null;
  }

  internal static IEnumerable<string> CommonInstallPaths() {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
      yield return @"C:\ffmpeg\bin\ffmpeg.exe";
      yield return @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
      yield return @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe";
      var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      if (!string.IsNullOrEmpty(localApp))
        yield return Path.Combine(localApp, "PhotoManager", "ffmpeg", "ffmpeg.exe");
      yield break;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
      yield return "/opt/homebrew/bin/ffmpeg";
      yield return "/usr/local/bin/ffmpeg";
      yield return "/usr/bin/ffmpeg";
      yield break;
    }

    yield return "/usr/bin/ffmpeg";
    yield return "/usr/local/bin/ffmpeg";
    yield return "/snap/bin/ffmpeg";
  }
}
