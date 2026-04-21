using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhotoManager.UI.Services;

/// <summary>
/// Opens a file path in the OS default handler, cross-platform.
/// Uses ShellExecute on Windows, "open" on macOS, "xdg-open" on Linux.
/// </summary>
public static class ShellLauncher {
  public static void OpenInDefaultViewer(string filePath) {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
      Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
      return;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
      Process.Start("open", new[] { filePath });
      return;
    }

    Process.Start("xdg-open", new[] { filePath });
  }
}
