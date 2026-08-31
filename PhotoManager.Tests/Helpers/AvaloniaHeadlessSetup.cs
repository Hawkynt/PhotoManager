using Avalonia;
using Avalonia.Headless;
using Hawkynt.PhotoManager.UI;

// Tells the Avalonia.Headless.NUnit test runner how to spin up an
// Application instance for any test that needs UI services. Lives at the
// assembly level so a single configuration applies to every test fixture.
[assembly: AvaloniaTestApplication(typeof(Hawkynt.PhotoManager.Tests.Helpers.HeadlessAppBuilderFactory))]

namespace Hawkynt.PhotoManager.Tests.Helpers;

/// <summary>
/// Builds a headless Avalonia App for the test runner — same configuration
/// the UI uses in production minus the desktop platform integrations,
/// substituted with the headless equivalents.
/// </summary>
public static class HeadlessAppBuilderFactory {
  public const string ScreenshotEnvironmentVariable = "PHOTOMANAGER_CAPTURE_SCREENSHOTS";

  public static AppBuilder BuildAvaloniaApp() {
    var builder = AppBuilder.Configure<App>();

    return Environment.GetEnvironmentVariable(ScreenshotEnvironmentVariable) == "1"
      ? builder
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
      : builder
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
  }
}
