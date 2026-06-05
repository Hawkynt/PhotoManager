using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Dialog shown right after the user draws a manual box on the preview.
/// Lets them pick a category (Person / Object / Other) and type an optional
/// label so the region is useful immediately rather than landing as an
/// untagged Proposed box that needs a second pass through the region list.
/// Returns a <see cref="Result"/> on OK; null on Cancel.
/// </summary>
public partial class RegionCategoryPickerWindow : Window {
  public RegionCategoryPickerWindow() {
    this.InitializeComponent();
  }

  public sealed record Result(RegionCategory Category, string? Label);

  private void OnOkClick(object? sender, RoutedEventArgs e) {
    var category = RegionCategory.Other;
    if (this.FindControl<RadioButton>("PersonRadio")?.IsChecked == true)
      category = RegionCategory.Person;
    else if (this.FindControl<RadioButton>("ObjectRadio")?.IsChecked == true)
      category = RegionCategory.Item;

    var label = this.FindControl<TextBox>("LabelBox")?.Text?.Trim();
    if (string.IsNullOrWhiteSpace(label))
      label = null;

    this.Close(new Result(category, label));
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);
}
