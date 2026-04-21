using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PhotoManager.UI.Views;
using PhotoManager.UI.Controllers;

namespace PhotoManager.UI;

public partial class App : Application {
  public override void Initialize() => AvaloniaXamlLoader.Load(this);

  public override void OnFrameworkInitializationCompleted() {
    if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      var controller = Program.Services.GetRequiredService<MainController>();
      var aboutController = Program.Services.GetRequiredService<AboutController>();
      desktop.MainWindow = new MainWindow(controller, aboutController);
    }

    base.OnFrameworkInitializationCompleted();
  }
}
