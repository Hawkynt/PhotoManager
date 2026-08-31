using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Hawkynt.PhotoManager.Core.Enums;
using Hawkynt.PhotoManager.Tests.Helpers;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Views;

namespace Hawkynt.PhotoManager.Tests.Unit.UI;

[TestFixture]
[Category("Screenshot")]
public class ReadmeScreenshotTests {
  [AvaloniaTest]
  public void ReadmeScreenshots_RenderCurrentUi() {
    if (Environment.GetEnvironmentVariable(HeadlessAppBuilderFactory.ScreenshotEnvironmentVariable) != "1")
      Assert.Ignore($"Set {HeadlessAppBuilderFactory.ScreenshotEnvironmentVariable}=1 to render README screenshots.");

    Assert.That(Application.Current, Is.Not.Null);
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

    var screenshotDirectory = Path.Combine(FindRepositoryRoot().FullName, "screenshots");
    Directory.CreateDirectory(screenshotDirectory);

    Capture(BuildMainWindow(), Path.Combine(screenshotDirectory, "main-window.png"));
    Capture(new EditImageWindow(), Path.Combine(screenshotDirectory, "develop-window.png"));
  }

  private static MainWindow BuildMainWindow() {
    var viewModel = new MainViewModel {
      DestinationDirectory = "/demo/Photos/Library",
      StatusMessage = "Ready — 4 photos indexed",
      DuplicateHandling = DuplicateHandling.Smart,
      PreserveOriginals = true
    };

    var window = new MainWindow {
      DataContext = viewModel
    };

    var incoming = new SourceTreeNode(new DirectoryInfo("/demo/Photos/Incoming"), isRecursive: true, isRoot: true);
    incoming.Children.Add(new SourceTreeNode(new DirectoryInfo("/demo/Photos/Incoming/2026-08 Berlin"), isRecursive: false, isRoot: false));
    incoming.Children.Add(new SourceTreeNode(new DirectoryInfo("/demo/Photos/Incoming/Family"), isRecursive: false, isRoot: false));

    Assert.That(window.FindControl<TreeView>("SourceTree"), Is.Not.Null);
    window.FindControl<TreeView>("SourceTree")!.ItemsSource = new[] { incoming };

    Assert.That(window.FindControl<ComboBox>("DuplicateHandlingCombo"), Is.Not.Null);
    window.FindControl<ComboBox>("DuplicateHandlingCombo")!.ItemsSource = Enum.GetValues<DuplicateHandling>();

    Assert.That(window.FindControl<DataGrid>("FilesGrid"), Is.Not.Null);
    window.FindControl<DataGrid>("FilesGrid")!.ItemsSource = new[] {
      new FileItemModel {
        FileName = "IMG_4821.CR3",
        TargetLocation = "2026/20260830/184223.CR3",
        SourcePath = "/demo/Photos/Incoming/2026-08 Berlin",
        IsPick = true,
        IsInQuickCollection = true,
        Rating = 5,
        ColorLabel = "Green"
      },
      new FileItemModel {
        FileName = "IMG_4822.CR3",
        TargetLocation = "2026/20260830/184227.CR3",
        SourcePath = "/demo/Photos/Incoming/2026-08 Berlin",
        Rating = 4,
        ColorLabel = "Blue"
      },
      new FileItemModel {
        FileName = "DSC_7714.NEF",
        TargetLocation = "2026/20260829/201105.NEF",
        SourcePath = "/demo/Photos/Incoming/Family",
        IsReject = true,
        Rating = -1
      },
      new FileItemModel {
        FileName = "PXL_20260828_175912.jpg",
        TargetLocation = "2026/20260828/175912.jpg",
        SourcePath = "/demo/Photos/Incoming/Family",
        Rating = 3,
        ColorLabel = "Yellow"
      }
    };

    return window;
  }

  private static void Capture(Window window, string path) {
    window.Show();
    try {
      var frame = window.CaptureRenderedFrame();
      Assert.That(frame, Is.Not.Null, $"Avalonia did not render {window.Title}.");
      frame!.Save(path);
      Assert.That(new FileInfo(path).Length, Is.GreaterThan(0), $"Screenshot {path} is empty.");
      TestContext.Progress.WriteLine($"Rendered {path}");
    } finally {
      window.Close();
    }
  }

  private static DirectoryInfo FindRepositoryRoot() {
    for (var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); current is not null; current = current.Parent) {
      if (File.Exists(Path.Combine(current.FullName, "PhotoManager.slnx")) && File.Exists(Path.Combine(current.FullName, "README.md")))
        return current;
    }

    throw new DirectoryNotFoundException("Could not locate the PhotoManager repository root from the test output directory.");
  }
}
