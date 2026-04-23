using System.CommandLine;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Metadata;

namespace PhotoManager.CLI;

/// <summary>
/// Builds the <c>faces</c> sub-command tree: list, tag. Writes go into the
/// sidecar's MWG-RS regions, interoperable with Lightroom/digiKam.
/// </summary>
internal static class FacesCommands {
  public static Command Build() {
    var faces = new Command("faces", "Read or tag face regions in the sidecar (MWG-RS format)");
    faces.AddCommand(BuildList());
    faces.AddCommand(BuildTag());
    return faces;
  }

  private static Command BuildList() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var cmd = new Command("list", "Show face regions stored in the sidecar") { fileArg };

    cmd.SetHandler(async file => {
      EnsureFile(file);

      var reader = new MetadataReader();
      var metadata = await reader.ReadAsync(file);

      if (metadata.Faces.Count == 0) {
        Console.WriteLine($"No face regions tagged in {SidecarPath.For(file).FullName}.");
        return;
      }

      Console.WriteLine($"{metadata.Faces.Count} face region(s):");
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      for (var i = 0; i < metadata.Faces.Count; i++) {
        var f = metadata.Faces[i];
        var name = string.IsNullOrEmpty(f.PersonName) ? "(unnamed)" : f.PersonName;
        Console.WriteLine(string.Create(inv,
          $"  [{i}] {name}  box=({f.Box.X:0.###},{f.Box.Y:0.###}) {f.Box.Width:0.###}x{f.Box.Height:0.###}"));
      }
    }, fileArg);

    return cmd;
  }

  private static Command BuildTag() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var indexOpt = new Option<int>("--index", "Zero-based face region index (see `faces list`)") { IsRequired = true };
    var nameOpt = new Option<string>("--name", "Person name to assign") { IsRequired = true };

    var cmd = new Command("tag", "Assign a person name to an existing face region") { fileArg, indexOpt, nameOpt };

    cmd.SetHandler(async (file, index, name) => {
      EnsureFile(file);

      var reader = new MetadataReader();
      var writer = new CompositeMetadataWriter();
      var registry = new PeopleRegistry();

      var service = new FaceRecognitionService(new NullFaceDetector(), reader, writer, registry);

      try {
        await service.TagFaceAsync(file, index, name);
        Console.WriteLine($"Tagged face [{index}] as '{name}' in {SidecarPath.For(file).FullName}");
      } catch (ArgumentOutOfRangeException ex) {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
      }
    }, fileArg, indexOpt, nameOpt);

    return cmd;
  }

  private static void EnsureFile(FileInfo file) {
    if (file.Exists)
      return;

    Console.Error.WriteLine($"Error: file '{file.FullName}' does not exist.");
    Environment.Exit(1);
  }
}
