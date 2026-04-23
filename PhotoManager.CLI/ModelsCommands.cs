using System.CommandLine;
using PhotoManager.Core;
using PhotoManager.Core.Models;

namespace PhotoManager.CLI;

/// <summary>
/// Builds the <c>models</c> sub-command tree: list the installable models,
/// download one, or print where they live on disk.
/// </summary>
internal static class ModelsCommands {
  public static Command Build() {
    var models = new Command("models", "Manage ONNX detection models (YOLO object detector, UltraFace face detector)");
    models.AddCommand(BuildList());
    models.AddCommand(BuildDownload());
    models.AddCommand(BuildPath());
    return models;
  }

  private static Command BuildList() {
    var cmd = new Command("list", "List the available detection models and whether each is installed");

    cmd.SetHandler(() => {
      Console.WriteLine($"Models directory: {AppDataPaths.SubDirectory("models").FullName}");
      Console.WriteLine();

      foreach (var model in ModelRegistry.All) {
        var installed = model.IsInstalled() ? "INSTALLED" : "missing";
        Console.WriteLine($"  {model.Name,-16}  {installed,-9}  {model.DisplayName}");
        Console.WriteLine($"    {model.Description}");
        Console.WriteLine($"    {model.DownloadUrl}");
      }
    });

    return cmd;
  }

  private static Command BuildDownload() {
    var nameArg = new Argument<string>("name", "Model name (see `models list`)");
    var cmd = new Command("download", "Download a model into the app-data models directory") { nameArg };

    cmd.SetHandler(async name => {
      var model = ModelRegistry.FindByName(name);
      if (model == null) {
        Console.Error.WriteLine($"Error: unknown model '{name}'. Run `models list` to see available models.");
        Environment.Exit(1);
        return;
      }

      Console.WriteLine($"Downloading {model.DisplayName}");
      Console.WriteLine($"  from: {model.DownloadUrl}");
      Console.WriteLine($"  to:   {model.ResolveDestination().FullName}");

      using var downloader = new ModelDownloader();
      var lastPercent = -1;

      var progress = new Progress<ModelDownloadProgress>(p => {
        var pct = p.Fraction is { } f ? (int)(f * 100) : -1;
        if (pct <= lastPercent)
          return;
        lastPercent = pct;
        if (pct < 0)
          Console.Write($"\r  {p.BytesReceived / 1024:0} KB received...");
        else
          Console.Write($"\r  {pct}%  ({p.BytesReceived / 1024:0} / {p.TotalBytes / 1024:0} KB)");
      });

      try {
        var file = await downloader.DownloadAsync(model, progress);
        Console.WriteLine();
        Console.WriteLine($"OK  {file.FullName}  ({file.Length / 1024:0} KB)");
      } catch (Exception ex) {
        Console.WriteLine();
        Console.Error.WriteLine($"Download failed: {ex.Message}");
        Console.Error.WriteLine($"If the URL has changed, place the file manually at:");
        Console.Error.WriteLine($"  {model.ResolveDestination().FullName}");
        Environment.Exit(1);
      }
    }, nameArg);

    return cmd;
  }

  private static Command BuildPath() {
    var cmd = new Command("path", "Print the app-data models directory path");
    cmd.SetHandler(() => {
      Console.WriteLine(AppDataPaths.SubDirectory("models").FullName);
    });
    return cmd;
  }
}
