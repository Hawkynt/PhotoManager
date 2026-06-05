using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Views;
using Hawkynt.PhotoManager.UI.Controllers;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI;

public partial class App : Application {
  public override void Initialize() => AvaloniaXamlLoader.Load(this);

  public override void OnFrameworkInitializationCompleted() {
    if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      var controller = Program.Services.GetRequiredService<MainController>();
      var aboutController = Program.Services.GetRequiredService<AboutController>();

      // ViewModel.Theme is the source of truth at runtime; LoadSettingsAsync
      // (fired on window open) seeds it from disk and our PropertyChanged
      // handler re-applies it. Apply current value once now so pre-load
      // state isn't a flash of the OS default.
      ThemeApplier.Apply(controller.ViewModel.Theme);
      controller.ViewModel.PropertyChanged += OnViewModelPropertyChanged;

      desktop.MainWindow = new MainWindow(controller, aboutController);
    }

    base.OnFrameworkInitializationCompleted();
  }

  private static void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName != nameof(MainViewModel.Theme) || sender is not MainViewModel vm)
      return;
    ThemeApplier.Apply(vm.Theme);
  }
}
