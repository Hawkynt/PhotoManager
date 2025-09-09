using System.Security.Cryptography;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Utilities;
using SystemIO = System.IO;

namespace PhotoManager.Core.Services;

public class FileOrganizer : IFileOrganizer {
  public async Task<FileInfo> GenerateTargetPath(FileToImport file, DateTime dateTime, ImportSettings settings) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(settings);
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
      return await Task.FromResult(new FileInfo(targetPath));

    // TODO: isnt this already handled in ProcessFileAsync?
    for (var i = 2; File.Exists(targetPath); ++i)
      targetPath = Path.Combine(directory, $"{baseFileName} ({i}){extension}");

    return await Task.FromResult(new FileInfo(targetPath));
  }

  public async Task<bool> MoveFileAsync(FileInfo sourcePath, FileInfo targetPath, bool overwrite = false) {
    ArgumentNullException.ThrowIfNull(sourcePath);
    ArgumentNullException.ThrowIfNull(targetPath);

    try {
      var targetDir = targetPath.DirectoryName;
      if (targetDir != null && !SystemIO.Directory.Exists(targetDir))
        SystemIO.Directory.CreateDirectory(targetDir);

      await Task.Run(() => File.Move(sourcePath.FullName, targetPath.FullName, overwrite));
      return true;
    } catch {
      return false;
    }
  }

  public async Task<bool> CopyFileAsync(FileInfo sourcePath, FileInfo targetPath, bool overwrite = false) {
    ArgumentNullException.ThrowIfNull(sourcePath);
    ArgumentNullException.ThrowIfNull(targetPath);

    try {
      var targetDir = targetPath.DirectoryName;
      if (targetDir != null && !SystemIO.Directory.Exists(targetDir))
        SystemIO.Directory.CreateDirectory(targetDir);

      await Task.Run(() => File.Copy(sourcePath.FullName, targetPath.FullName, overwrite));
      return true;
    } catch {
      return false;
    }
  }

  public async Task<bool> AreFilesIdenticalAsync(FileInfo filePath1, FileInfo filePath2) {
    ArgumentNullException.ThrowIfNull(filePath1);
    ArgumentNullException.ThrowIfNull(filePath2);

    if (!filePath1.Exists || !filePath2.Exists)
      return false;

    // Quick size check first
    if (filePath1.Length != filePath2.Length)
      return false;

    // For small files, do a byte-by-byte comparison
    if (filePath1.Length < 100 * 1024 * 1024) { // 100MB
      var bytes1 = await File.ReadAllBytesAsync(filePath1.FullName);
      var bytes2 = await File.ReadAllBytesAsync(filePath2.FullName);
      return bytes1.SequenceEqual(bytes2);
    }

    // For larger files, use SHA-256 hash comparison
    // TODO: we don't trust collisions, so maybe do a byte-by-byte. but be clever, compare the first and the last chunk first
    using var sha256 = SHA256.Create();
    
    byte[] hash1;
    await using (var stream1 = File.OpenRead(filePath1.FullName))
      hash1 = await sha256.ComputeHashAsync(stream1);

    byte[] hash2;
    await using (var stream2 = File.OpenRead(filePath2.FullName))
      hash2 = await sha256.ComputeHashAsync(stream2);

    return hash1.SequenceEqual(hash2);
  }

  public async Task<(FileOperationResult result, FileInfo? targetPath, string? message)> ProcessFileAsync(
    FileToImport file, DateTime dateTime, ImportSettings settings) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(settings);

    try {
      var targetPath = await this.GenerateTargetPath(file, dateTime, settings);
      var sourcePath = file.Source;

      // Handle dry run
      if (settings.DryRun) {
        if (!targetPath.Exists)
          return (FileOperationResult.Success, targetPath, "Would process normally");

        var areIdentical = await this.AreFilesIdenticalAsync(sourcePath, targetPath);
        var message = areIdentical ? "Would skip (identical file exists)" : "Would process with conflict handling";
        return (FileOperationResult.Success, targetPath, message);
      }

      // Check if target already exists
      if (targetPath.Exists) {
        switch (settings.DuplicateHandling) {
          case DuplicateHandling.Skip:
            return (FileOperationResult.Skipped, targetPath, "File already exists, skipped");

          case DuplicateHandling.Smart:
            var areIdentical = await this.AreFilesIdenticalAsync(sourcePath, targetPath);
            if (areIdentical) {
              if (settings.PreserveOriginals)
                return (FileOperationResult.Skipped, targetPath, "Identical file exists, source preserved");

              // Files are identical, just remove the source
              File.Delete(sourcePath.FullName);
              return (FileOperationResult.DuplicateRemoved, targetPath, "Identical file exists, source removed");
            }
            // Files are different, fall through to rename logic
            goto case DuplicateHandling.Rename;

          case DuplicateHandling.Rename:
            // Generate new path with number suffix
            var directory = targetPath.DirectoryName!;
            var baseFileName = Path.GetFileNameWithoutExtension(targetPath.FullName);
            var extension = targetPath.Extension;
            
            for (var i = 2; targetPath.Exists; ++i) {
              var newPath = Path.Combine(directory, $"{baseFileName} ({i}){extension}");
              targetPath = new FileInfo(newPath);
            }
            break;

          case DuplicateHandling.Overwrite:
            // Will overwrite in the operation below
            break;
        }
      }

      // Ensure target directory exists
      var targetDir = targetPath.DirectoryName;
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
