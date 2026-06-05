using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Hawkynt.PhotoManager.Core.Interfaces;
using Hawkynt.PhotoManager.Core.Services;
using Hawkynt.PhotoManager.UI.Controllers;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI;

internal static class Program {
  public static IServiceProvider Services { get; private set; } = null!;

  [STAThread]
  public static void Main(string[] args) {
    Services = ConfigureServices();
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
  }

  public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace()
      .UseReactiveUI();

  private static IServiceProvider ConfigureServices() {
    var services = new ServiceCollection();

    services.AddSingleton<IImportManager, ImportManager>();
    services.AddSingleton<IDateTimeParser, DateTimeParser>();
    services.AddSingleton<IFileOrganizer, FileOrganizer>();

    services.AddSingleton<ISettingsService, SettingsService>();
    services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();

    services.AddSingleton<MainViewModel>();
    services.AddSingleton<MainController>();
    services.AddTransient<AboutController>();

    return services.BuildServiceProvider();
  }
}
