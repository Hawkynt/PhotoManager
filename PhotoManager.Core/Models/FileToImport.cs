using System.Globalization;

namespace PhotoManager.Core.Models;

public class FileToImport(FileInfo source) {
  private IReadOnlyList<Directory>? _metadata;
  private DateTime? _cachedFileCreatedAt;
  private DateTime? _cachedFileLastWrittenAt;
  private readonly Dictionary<string, List<DateTime>> _cachedDateResults = new();
  private DateTime? _lastModified;

  public FileInfo Source => source;
  public string FileName => this.Source.Name;

  public async Task<DateTime> GetFileCreatedAt() {
    if (!this._cachedFileCreatedAt.HasValue || this.HasFileChanged()) {
      this._cachedFileCreatedAt = await Task.Run(() => {
        this.Source.Refresh(); // Ensure we have latest file info
        return this.Source.CreationTime;
      });
      this.UpdateLastModified();
    }
    return this._cachedFileCreatedAt.Value;
  }
  
  public async Task<DateTime> GetFileLastWrittenAt() {
    if (!this._cachedFileLastWrittenAt.HasValue || this.HasFileChanged()) {
      this._cachedFileLastWrittenAt = await Task.Run(() => {
        this.Source.Refresh(); // Ensure we have latest file info
        return this.Source.LastWriteTime;
      });
      this.UpdateLastModified();
    }
    return this._cachedFileLastWrittenAt.Value;
  }

  private async Task EnsureMetadataReadAsync() {
    if (this._metadata == null || this.HasFileChanged()) {
      this._metadata = await Task.Run(() => ImageMetadataReader.ReadMetadata(this.Source.FullName));
      this.UpdateLastModified();
      this._cachedDateResults.Clear(); // Clear cached EXIF/GPS results since metadata changed
    }
  }

  public async IAsyncEnumerable<DateTime> GetExifIfd0DateAsync() {
    const string cacheKey = "ExifIfd0";
    
    if (this._cachedDateResults.TryGetValue(cacheKey, out var cached) && !this.HasFileChanged()) {
      foreach (var date in cached)
        yield return date;
      yield break;
    }

    await this.EnsureMetadataReadAsync();
    
    var results = new List<DateTime>();
    foreach (var directory in this._metadata!.OfType<ExifIfd0Directory>()) {
      if (directory.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var result)) {
        results.Add(result);
        yield return result;
      }
    }
    
    this._cachedDateResults[cacheKey] = results;
  }

  public async IAsyncEnumerable<DateTime> GetExifSubIfdDateAsync() {
    const string cacheKey = "ExifSubIfd";
    
    if (this._cachedDateResults.TryGetValue(cacheKey, out var cached) && !this.HasFileChanged()) {
      foreach (var date in cached)
        yield return date;
      yield break;
    }

    await this.EnsureMetadataReadAsync();
    
    var results = new List<DateTime>();
    foreach (var directory in this._metadata!.OfType<ExifSubIfdDirectory>()) {
      if (directory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var result)) {
        results.Add(result);
        yield return result;
      }
    }
    
    this._cachedDateResults[cacheKey] = results;
  }

  public async IAsyncEnumerable<DateTime> GetGpsDateAsync() {
    const string cacheKey = "Gps";
    
    if (this._cachedDateResults.TryGetValue(cacheKey, out var cached) && !this.HasFileChanged()) {
      foreach (var date in cached)
        yield return date;
      yield break;
    }

    await this.EnsureMetadataReadAsync();
    
    var results = new List<DateTime>();
    foreach (var directory in this._metadata!.OfType<GpsDirectory>()) {
      var date = directory.GetDescription(GpsDirectory.TagDateStamp);
      var time = directory.GetDescription(GpsDirectory.TagTimeStamp);

      if (DateTime.TryParseExact($"{date} {time}", "yyyy:MM:dd HH:mm:ss.fff", null, DateTimeStyles.None, out var result)) {
        results.Add(result);
        yield return result;
      }
    }
    
    this._cachedDateResults[cacheKey] = results;
  }

  private bool HasFileChanged() {
    this.Source.Refresh();
    return !this._lastModified.HasValue || this.Source.LastWriteTime > this._lastModified.Value;
  }

  private void UpdateLastModified() {
    this.Source.Refresh();
    this._lastModified = this.Source.LastWriteTime;
  }
}
