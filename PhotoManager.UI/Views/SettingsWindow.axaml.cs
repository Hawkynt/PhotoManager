using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoManager.Core;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Single-window preferences dialog. Bound directly to <see cref="MainViewModel"/>;
/// every two-way binding feeds back into the running view-model so the next
/// <c>SaveSettingsAsync</c> persists the change. The window itself triggers
/// a save on close so settings never get lost when users hit "X".
/// </summary>
public partial class SettingsWindow : Window {
  private readonly MainController? _controller;

  public SettingsWindow() : this(null) { }

  public SettingsWindow(MainController? controller) {
    this.InitializeComponent();
    this._controller = controller;

    if (controller != null) {
      this.DataContext = controller.ViewModel;
      this.SyncThemeRadios();
      controller.ViewModel.PropertyChanged += (_, e) => {
        if (e.PropertyName == nameof(MainViewModel.Theme))
          this.SyncThemeRadios();
      };
    }

    if (this.FindControl<TextBox>("ModelFolderText") is { } modelText)
      modelText.Text = AppDataPaths.SubDirectory("models").FullName;

    this.Closing += (_, _) => {
      if (this._controller != null)
        _ = this._controller.SaveSettingsAsync();
    };
  }

  private void SyncThemeRadios() {
    if (this._controller == null)
      return;
    var t = this._controller.ViewModel.Theme;
    if (this.FindControl<RadioButton>("ThemeLightRadio") is { } lr) lr.IsChecked = t == ThemeVariantPreference.Light;
    if (this.FindControl<RadioButton>("ThemeDarkRadio") is { } dr) dr.IsChecked = t == ThemeVariantPreference.Dark;
    if (this.FindControl<RadioButton>("ThemeSystemRadio") is { } sr) sr.IsChecked = t == ThemeVariantPreference.System;
  }

  private void OnThemeLightClick(object? sender, RoutedEventArgs e)
    => this._controller?.SetTheme(ThemeVariantPreference.Light);

  private void OnThemeDarkClick(object? sender, RoutedEventArgs e)
    => this._controller?.SetTheme(ThemeVariantPreference.Dark);

  private void OnThemeSystemClick(object? sender, RoutedEventArgs e)
    => this._controller?.SetTheme(ThemeVariantPreference.System);

  private void OnOpenModelFolderClick(object? sender, RoutedEventArgs e) {
    var dir = AppDataPaths.SubDirectory("models");
    try {
      ShellLauncher.OpenInDefaultViewer(dir.FullName);
    } catch {
      // Best-effort; failure here only means we couldn't pop the OS file
      // browser — the path is already shown in the read-only field above.
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();
}
