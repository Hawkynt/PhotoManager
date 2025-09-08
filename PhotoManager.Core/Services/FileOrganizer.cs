using System.Security.Cryptography;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using SystemIO = System.IO;

namespace PhotoManager.Core.Services;

public class FileOrganizer : IFileOrganizer {
  public async Task<string> GenerateTargetPath(FileToImport file, DateTime dateTime, ImportSettings settings) {
    var destinationDir = settings.DestinationDirectory ?? settings.SourceDirectory;

    // Parse the date format pattern to build the path
    var pathParts = new List<string> { destinationDir.FullName };

    // Default pattern: "yyyy/yyyyMMdd/HHmmss"
    var patterns = settings.DateFormatPattern.Split('/');
    pathParts.AddRange(patterns.Take(patterns.Length - 1).Select(dateTime.ToString));

    var directory = Path.Combine(pathParts.ToArray());
    var fileNamePattern = patterns.Last();
    var baseFileName = dateTime.ToString(fileNamePattern);
    var extension = file.Source.Extension;

    var targetPath = Path.Combine(directory, $"{baseFileName}{extension}");

    // Handle duplicates based on strategy
    if (settings.DuplicateHandling != DuplicateHandling.Rename)
      return await Task.FromResult(targetPath);

    for (var i = 2; File.Exists(targetPath); ++i)
      targetPath = Path.Combine(directory, $"{baseFileName} ({i}){extension}");

    return await Task.FromResult(targetPath);
  }

  public async Task<bool> MoveFileAsync(string sourcePath, string targetPath, bool overwrite = false) {
    try {
      var targetDir = Path.GetDirectoryName(targetPath);
      if (targetDir != null && !SystemIO.Directory.Exists(targetDir))
        SystemIO.Directory.CreateDirectory(targetDir);

      await Task.Run(() => File.Move(sourcePath, targetPath, overwrite));
      return true;
    } catch {
      return false;
    }
  }

  public async Task<bool> CopyFileAsync(string sourcePath, string targetPath, bool overwrite = false) {
    try {
      var targetDir = Path.GetDirectoryName(targetPath);
      if (targetDir != null && !SystemIO.Directory.Exists(targetDir))
        SystemIO.Directory.CreateDirectory(targetDir);

      await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite));
      return true;
    } catch {
      return false;
    }
  }

  public async Task<bool> AreFilesIdenticalAsync(string filePath1, string filePath2) {
    if (!File.Exists(filePath1) || !File.Exists(filePath2))
      return false;

    var file1Info = new FileInfo(filePath1);
    var file2Info = new FileInfo(filePath2);

    // Quick size check first
    if (file1Info.Length != file2Info.Length)
      return false;

    // For small files, do a byte-by-byte comparison
    if (file1Info.Length < 100 * 1024 * 1024) { // 100MB
      var bytes1 = await File.ReadAllBytesAsync(filePath1);
      var bytes2 = await File.ReadAllBytesAsync(filePath2);
      return bytes1.SequenceEqual(bytes2);
    }

    // For larger files, use SHA-256 hash comparison
    using var sha256 = SHA256.Create();
    
    byte[] hash1;
    await using (var stream1 = File.OpenRead(filePath1))
      hash1 = await sha256.ComputeHashAsync(stream1);

    byte[] hash2;
    await using (var stream2 = File.OpenRead(filePath2))
      hash2 = await sha256.ComputeHashAsync(stream2);

    return hash1.SequenceEqual(hash2);
  }

  public async Task<(FileOperationResult result, string? targetPath, string? message)> ProcessFileAsync(
    FileToImport file, DateTime dateTime, ImportSettings settings) {
    try {
      var targetPath = await this.GenerateTargetPath(file, dateTime, settings);
      var sourcePath = file.Source.FullName;

      // Handle dry run
      if (settings.DryRun) {
        if (!File.Exists(targetPath))
          return (FileOperationResult.Success, targetPath, "Would process normally");

        var areIdentical = await this.AreFilesIdenticalAsync(sourcePath, targetPath);
        var message = areIdentical ? "Would skip (identical file exists)" : "Would process with conflict handling";
        return (FileOperationResult.Success, targetPath, message);
      }

      // Check if target already exists
      if (File.Exists(targetPath)) {
        switch (settings.DuplicateHandling) {
          case DuplicateHandling.Skip:
            return (FileOperationResult.Skipped, targetPath, "File already exists, skipped");

          case DuplicateHandling.Smart:
            var areIdentical = await this.AreFilesIdenticalAsync(sourcePath, targetPath);
            if (areIdentical) {
              if (settings.PreserveOriginals)
                return (FileOperationResult.Skipped, targetPath, "Identical file exists, source preserved");

              // Files are identical, just remove the source
              File.Delete(sourcePath);
              return (FileOperationResult.DuplicateRemoved, targetPath, "Identical file exists, source removed");
            }
            // Files are different, fall through to rename logic
            goto case DuplicateHandling.Rename;

          case DuplicateHandling.Rename:
            // Generate new path with number suffix
            var directory = Path.GetDirectoryName(targetPath)!;
            var baseFileName = Path.GetFileNameWithoutExtension(targetPath);
            var extension = Path.GetExtension(targetPath);
            
            for (var i = 2; File.Exists(targetPath); ++i)
              targetPath = Path.Combine(directory, $"{baseFileName} ({i}){extension}");
            break;

          case DuplicateHandling.Overwrite:
            // Will overwrite in the operation below
            break;
        }
      }

      // Ensure target directory exists
      var targetDir = Path.GetDirectoryName(targetPath);
      if (targetDir != null && !SystemIO.Directory.Exists(targetDir))
        SystemIO.Directory.CreateDirectory(targetDir);

      // Perform the file operation
      bool success;
      if (settings.PreserveOriginals)
        success = await this.CopyFileAsync(sourcePath, targetPath, settings.DuplicateHandling == DuplicateHandling.Overwrite);
      else
        success = await this.MoveFileAsync(sourcePath, targetPath, settings.DuplicateHandling == DuplicateHandling.Overwrite);

      return success 
        ? (FileOperationResult.Success, targetPath, null) 
        : (FileOperationResult.Failed, targetPath, "File operation failed")
        ;

    } catch (Exception ex) {
      return (FileOperationResult.Failed, null, ex.Message);
    }
  }
}
