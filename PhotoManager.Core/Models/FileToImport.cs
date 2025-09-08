using System.Globalization;

namespace PhotoManager.Core.Models;

public class FileToImport(FileInfo source) {
  private IReadOnlyList<Directory>? _metadata;

  public FileInfo Source => source;
  public string FileName => this.Source.Name;

  public async Task<DateTime> GetFileCreatedAt() => await Task.Run(() => this.Source.CreationTime);
  public async Task<DateTime> GetFileLastWrittenAt() => await Task.Run(() => this.Source.LastWriteTime);

  private async Task EnsureMetadataReadAsync() => this._metadata ??= await Task.Run(() => ImageMetadataReader.ReadMetadata(this.Source.FullName));

  public async IAsyncEnumerable<DateTime> GetExifIfd0DateAsync() {
    await this.EnsureMetadataReadAsync();

    foreach (var directory in this._metadata!.OfType<ExifIfd0Directory>())
      if (directory.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var result))
        yield return result;
  }

  public async IAsyncEnumerable<DateTime> GetExifSubIfdDateAsync() {
    await this.EnsureMetadataReadAsync();

    foreach (var directory in this._metadata!.OfType<ExifSubIfdDirectory>())
      if (directory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var result))
        yield return result;
  }

  public async IAsyncEnumerable<DateTime> GetGpsDateAsync() {
    await this.EnsureMetadataReadAsync();

    foreach (var directory in this._metadata!.OfType<GpsDirectory>()) {
      var date = directory.GetDescription(GpsDirectory.TagDateStamp);
      var time = directory.GetDescription(GpsDirectory.TagTimeStamp);

      if (DateTime.TryParseExact($"{date} {time}", "yyyy:MM:dd HH:mm:ss.fff", null, DateTimeStyles.None, out var result))
        yield return result;
    }
  }
}
