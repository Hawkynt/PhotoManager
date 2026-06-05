using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.UI.Controllers;

namespace Hawkynt.PhotoManager.UI.Views;

public partial class AboutWindow : Window {
  public AboutWindow() : this(new AboutController()) { }

  public AboutWindow(AboutController controller) {
    this.InitializeComponent();

    var info = controller.GetAboutInfo();
    this.FindControl<TextBlock>("TitleText")!.Text = info.Title;
    this.FindControl<TextBlock>("VersionText")!.Text = $"Version {info.Version}";
    this.FindControl<TextBlock>("CopyrightText")!.Text = info.Copyright;
    this.FindControl<TextBlock>("DescriptionText")!.Text = info.Description;
  }

  private void OnOkClick(object? sender, RoutedEventArgs e) => this.Close();
}
