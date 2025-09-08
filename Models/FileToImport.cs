using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

using Directory=MetadataExtractor.Directory;

namespace PhotoManager.Models;

public class FileToImport(FileInfo source) {
    private IReadOnlyList<Directory>? _metadata;

    public FileInfo Source => source;
    public string FileName => this.Source.Name;
    
    public async Task<DateTime> GetFileCreatedAt() => await Task.Run(()=>this.Source.CreationTime);
    public async Task<DateTime> GetFileLastWrittenAt() => await Task.Run(()=>this.Source.LastWriteTime);

    private async Task EnsureMetadataReadAsync() {
        if (_metadata == null)
            _metadata = await Task.Run(() => ImageMetadataReader.ReadMetadata(Source.FullName));
    }

    public async IAsyncEnumerable<DateTime> GetExifIfd0DateAsync() {
        await EnsureMetadataReadAsync();

        foreach (var directory in _metadata!.OfType<ExifIfd0Directory>())
            if(directory.TryGetDateTime(ExifDirectoryBase.TagDateTime, out DateTime result))
                yield return result;
    }

    public async IAsyncEnumerable<DateTime> GetExifSubIfdDateAsync() {
        await EnsureMetadataReadAsync();

        foreach (var directory in _metadata!.OfType<ExifSubIfdDirectory>())
            if(directory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime result))
                yield return result;
    }

    public async IAsyncEnumerable<DateTime> GetGpsDateAsync()
    {
        await EnsureMetadataReadAsync();

        foreach (var directory in _metadata!.OfType<GpsDirectory>()) {
            var date = directory.GetDescription(GpsDirectory.TagDateStamp);
            var time = directory.GetDescription(GpsDirectory.TagTimeStamp);

            if (DateTime.TryParseExact($"{date} {time}", "yyyy:MM:dd HH:mm:ss.fff", null, System.Globalization.DateTimeStyles.None, out DateTime result))
                yield return result;
        }
    }
}