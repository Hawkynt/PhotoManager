namespace PhotoManager.Core.Interfaces;

public interface ISupportedFormatsService {
  /// <summary>
  /// Gets all supported file extensions for metadata extraction
  /// </summary>
  /// <returns>Array of file extensions with wildcards (e.g., "*.jpg")</returns>
  Task<string[]> GetSupportedExtensionsAsync();
  
  /// <summary>
  /// Gets all supported file extensions for metadata extraction without wildcards
  /// </summary>
  /// <returns>Array of file extensions (e.g., ".jpg")</returns>
  Task<string[]> GetSupportedExtensionsWithoutWildcardsAsync();
  
  /// <summary>
  /// Checks if a file extension is supported for metadata extraction
  /// </summary>
  /// <param name="extension">File extension to check (with or without dot)</param>
  /// <returns>True if supported, false otherwise</returns>
  bool IsExtensionSupported(string extension);
  
  /// <summary>
  /// Gets supported extensions grouped by format type
  /// </summary>
  /// <returns>Dictionary with format type as key and extensions as values</returns>
  Task<Dictionary<string, string[]>> GetExtensionsByFormatAsync();
}