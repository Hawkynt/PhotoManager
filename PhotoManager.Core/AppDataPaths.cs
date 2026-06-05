namespace Hawkynt.PhotoManager.Core;

/// <summary>
/// Cross-platform resolution for the app's data directory — a settings/cache
/// bucket separate from the user's photo library.
///   Windows: %APPDATA%\PhotoManager
///   macOS:   ~/Library/Application Support/PhotoManager
///   Linux:   ~/.config/PhotoManager (respects XDG_CONFIG_HOME)
/// </summary>
public static class AppDataPaths {
  public const string ProductName = "PhotoManager";

  /// <summary>
  /// Returns the root directory for app data, creating it if needed.
  /// </summary>
  public static DirectoryInfo Root() {
    var dir = new DirectoryInfo(ComputeRoot());
    dir.Create();
    return dir;
  }

  /// <summary>
  /// Resolves a subdirectory under the app data root (e.g. "models"), creating it.
  /// </summary>
  public static DirectoryInfo SubDirectory(string name) {
    var dir = new DirectoryInfo(Path.Combine(Root().FullName, name));
    dir.Create();
    return dir;
  }

  public static FileInfo ModelFile(string fileName)
    => new(Path.Combine(SubDirectory("models").FullName, fileName));

  private static string ComputeRoot() {
    if (OperatingSystem.IsWindows()) {
      return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductName);
    }

    if (OperatingSystem.IsMacOS()) {
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      return Path.Combine(home, "Library", "Application Support", ProductName);
    }

    // Linux / BSD — follow XDG Base Directory if set, otherwise ~/.config
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    if (!string.IsNullOrEmpty(xdg))
      return Path.Combine(xdg, ProductName);

    var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(userHome, ".config", ProductName);
  }
}
