using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Hawkynt.PhotoManager.UI.Views;

namespace Hawkynt.PhotoManager.Tests.Unit.UI;

[TestFixture]
public class HeadlessSmokeTests {
  /// <summary>
  /// Smoke-test: instantiate a couple of low-dependency dialogs in headless
  /// mode and confirm they construct + show without throwing. Validates
  /// the headless harness is wired correctly; deeper interaction tests
  /// build on top of this.
  /// </summary>
  [AvaloniaTest]
  public void InputDialogWindow_ConstructsAndShowsTitle() {
    var w = new InputDialogWindow("My title", "Type something:", "default-value");
    w.Show();
    try {
      Assert.Multiple(() => {
        Assert.That(w.Title, Is.EqualTo("My title"));
        Assert.That(w.FindControl<TextBox>("InputBox")?.Text, Is.EqualTo("default-value"));
      });
    } finally {
      w.Close();
    }
  }

  [AvaloniaTest]
  public void RegionCategoryPickerWindow_ConstructsCleanly() {
    var w = new RegionCategoryPickerWindow();
    w.Show();
    try {
      Assert.That(w.IsLoaded || w.IsInitialized, Is.True);
    } finally {
      w.Close();
    }
  }

  [AvaloniaTest]
  public void EditImageWindow_ConstructsWithoutSourceFile() {
    // Repros the crash path: opening Develop with no file selected used to
    // blow up because the ComboBox SelectedIndex set in XAML fires
    // SelectionChanged during InitializeComponent, before the visual tree
    // is fully wired.
    var w = new EditImageWindow();
    w.Show();
    try {
      Assert.That(w.IsLoaded || w.IsInitialized, Is.True);
    } finally {
      w.Close();
    }
  }
}
