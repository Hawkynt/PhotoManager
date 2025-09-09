using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PhotoManager.Core.Utilities;

internal static class ThrowHelpers {
  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static void ThrowDirectoryNotFoundException(string path)
    => throw new DirectoryNotFoundException($"Directory not found: {path}");

  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static void ThrowFileNotFoundException(string path)
    => throw new FileNotFoundException($"File not found: {path}", path);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ThrowIfDirectoryNotExists(DirectoryInfo directory, [CallerArgumentExpression(nameof(directory))] string? paramName = null) {
    ArgumentNullException.ThrowIfNull(directory, paramName);
    if (!directory.Exists)
      ThrowDirectoryNotFoundException(directory.FullName);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void ThrowIfFileNotExists(FileInfo file, [CallerArgumentExpression(nameof(file))] string? paramName = null) {
    ArgumentNullException.ThrowIfNull(file, paramName);
    if (!file.Exists)
      ThrowFileNotFoundException(file.FullName);
  }
}