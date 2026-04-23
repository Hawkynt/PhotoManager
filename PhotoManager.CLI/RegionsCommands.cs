using System.CommandLine;
using System.Globalization;
using PhotoManager.Core;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;

namespace PhotoManager.CLI;

/// <summary>
/// Builds the <c>regions</c> sub-command tree: list, propose, accept,
/// discard, add, relabel. Regions are the generalized replacement for
/// face-only tagging — they cover persons, animals, items, places,
/// with categories that colour-code the UI overlay.
/// </summary>
internal static class RegionsCommands {
  public static Command Build() {
    var regions = new Command("regions", "List and edit tagged regions (persons, animals, items, places)");
    regions.AddCommand(BuildList());
    regions.AddCommand(BuildPropose());
    regions.AddCommand(BuildAccept());
    regions.AddCommand(BuildDiscard());
    regions.AddCommand(BuildAdd());
    regions.AddCommand(BuildRelabel());
    return regions;
  }

  private static Command BuildList() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var cmd = new Command("list", "Show all tagged regions in the sidecar") { fileArg };

    cmd.SetHandler(async file => {
      EnsureFile(file);
      var service = BuildService();
      var regions = await service.ListAsync(file);

      if (regions.Count == 0) {
        Console.WriteLine($"No regions tagged in {SidecarPath.For(file).FullName}.");
        return;
      }

      var inv = CultureInfo.InvariantCulture;
      Console.WriteLine($"{regions.Count} region(s):");
      for (var i = 0; i < regions.Count; i++) {
        var r = regions[i];
        var label = string.IsNullOrEmpty(r.Label) ? "(unnamed)" : r.Label;
        Console.WriteLine(string.Create(inv,
          $"  [{i}] {r.Category} {r.Status,-8} {label}  box=({r.Box.X:0.###},{r.Box.Y:0.###}) {r.Box.Width:0.###}x{r.Box.Height:0.###}{(string.IsNullOrEmpty(r.Source) ? "" : $"  src={r.Source}")}"));
      }
    }, fileArg);

    return cmd;
  }

  private static Command BuildPropose() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var cmd = new Command("propose", "Run the object detector and add any findings as Proposed regions") { fileArg };

    cmd.SetHandler(async file => {
      EnsureFile(file);

      using var yolo = new YoloObjectDetector();
      var proposer = new YoloRegionProposer(yolo);
      var proposed = await proposer.ProposeAsync(file);

      if (!yolo.IsAvailable) {
        Console.WriteLine($"(YOLO model not found at {AppDataPaths.ModelFile(YoloObjectDetector.DefaultModelFileName).FullName} — nothing proposed.)");
        return;
      }

      if (proposed.Count == 0) {
        Console.WriteLine("Detector found nothing above threshold.");
        return;
      }

      var service = BuildService();
      await service.AppendAsync(file, proposed);

      Console.WriteLine($"Proposed {proposed.Count} region(s). Run `regions list {file.FullName}` to review.");
    }, fileArg);

    return cmd;
  }

  private static Command BuildAccept() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var indexOpt = new Option<int>("--index", "Zero-based region index from `regions list`") { IsRequired = true };

    var cmd = new Command("accept", "Mark a proposed region as accepted (adds its label to keywords)") { fileArg, indexOpt };
    cmd.SetHandler(async (file, index) => {
      EnsureFile(file);
      try {
        await BuildService().AcceptAsync(file, index);
        Console.WriteLine($"Accepted region [{index}].");
      } catch (ArgumentOutOfRangeException ex) {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
      }
    }, fileArg, indexOpt);

    return cmd;
  }

  private static Command BuildDiscard() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var indexOpt = new Option<int>("--index", "Zero-based region index") { IsRequired = true };

    var cmd = new Command("discard", "Delete a region from the sidecar") { fileArg, indexOpt };
    cmd.SetHandler(async (file, index) => {
      EnsureFile(file);
      try {
        await BuildService().DiscardAsync(file, index);
        Console.WriteLine($"Discarded region [{index}].");
      } catch (ArgumentOutOfRangeException ex) {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
      }
    }, fileArg, indexOpt);

    return cmd;
  }

  private static Command BuildAdd() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var xOpt = InvariantFloatOption("--x", "Box top-left X (0..1)");
    var yOpt = InvariantFloatOption("--y", "Box top-left Y (0..1)");
    var wOpt = InvariantFloatOption("--w", "Box width (0..1)");
    var hOpt = InvariantFloatOption("--h", "Box height (0..1)");
    var labelOpt = new Option<string>("--label", "Label (e.g. 'Alice', 'cat', 'spoon')") { IsRequired = true };
    var categoryOpt = new Option<RegionCategory>("--category", getDefaultValue: () => RegionCategory.Other) {
      Description = "Category: Person | Animal | Item | Place | Other"
    };

    var cmd = new Command("add", "Manually add a labeled region") { fileArg, xOpt, yOpt, wOpt, hOpt, labelOpt, categoryOpt };
    cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) => {
      var file = ctx.ParseResult.GetValueForArgument(fileArg);
      var x = ctx.ParseResult.GetValueForOption(xOpt);
      var y = ctx.ParseResult.GetValueForOption(yOpt);
      var w = ctx.ParseResult.GetValueForOption(wOpt);
      var h = ctx.ParseResult.GetValueForOption(hOpt);
      var label = ctx.ParseResult.GetValueForOption(labelOpt)!;
      var category = ctx.ParseResult.GetValueForOption(categoryOpt);

      EnsureFile(file);

      var region = new TaggedRegion(
        new NormalizedBoundingBox(x, y, w, h),
        category,
        label,
        RegionStatus.Accepted,
        TaggedRegion.ManualSource
      );

      await BuildService().AppendAsync(file, new[] { region });
      Console.WriteLine($"Added region: {category} '{label}'.");
    });

    return cmd;
  }

  private static Command BuildRelabel() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var indexOpt = new Option<int>("--index", "Zero-based region index") { IsRequired = true };
    var labelOpt = new Option<string>("--label", "New label") { IsRequired = true };

    var cmd = new Command("relabel", "Rename a region") { fileArg, indexOpt, labelOpt };
    cmd.SetHandler(async (file, index, label) => {
      EnsureFile(file);
      try {
        await BuildService().RelabelAsync(file, index, label);
        Console.WriteLine($"Relabeled region [{index}] → '{label}'.");
      } catch (ArgumentOutOfRangeException ex) {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
      }
    }, fileArg, indexOpt, labelOpt);

    return cmd;
  }

  private static RegionService BuildService()
    => new(new MetadataReader(), new CompositeMetadataWriter());

  private static Option<float> InvariantFloatOption(string name, string description) {
    return new Option<float>(name, result => {
      var token = result.Tokens[0].Value;
      return float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
    }, isDefault: false, description) { IsRequired = true };
  }

  private static void EnsureFile(FileInfo file) {
    if (file.Exists)
      return;

    Console.Error.WriteLine($"Error: file '{file.FullName}' does not exist.");
    Environment.Exit(1);
  }
}
