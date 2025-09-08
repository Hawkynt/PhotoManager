namespace PhotoManager.Core.Enums;

public enum DuplicateHandling : byte {
  /// <summary>
  /// Rename the file with a number suffix (default behavior)
  /// </summary>
  Rename,
  
  /// <summary>
  /// Skip the file if destination exists
  /// </summary>
  Skip,
  
  /// <summary>
  /// Overwrite the existing file
  /// </summary>
  Overwrite,
  
  /// <summary>
  /// Smart handling: Skip if identical, rename if different
  /// </summary>
  Smart
}