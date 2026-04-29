using PhotoManager.Core.Metadata;
using PhotoManager.UI.Services;

namespace PhotoManager.Tests.Unit.Services;

[TestFixture]
public class PickRejectHotkeysTests {
  [TestCase('p', PickRejectHotkeyAction.Pick)]
  [TestCase('P', PickRejectHotkeyAction.Pick)]
  [TestCase('x', PickRejectHotkeyAction.Reject)]
  [TestCase('X', PickRejectHotkeyAction.Reject)]
  [TestCase('u', PickRejectHotkeyAction.Clear)]
  [TestCase('U', PickRejectHotkeyAction.Clear)]
  [TestCase('a', PickRejectHotkeyAction.None)]
  [TestCase(' ', PickRejectHotkeyAction.None)]
  public void Resolve_MapsBoundKeys(char key, PickRejectHotkeyAction expected) {
    Assert.That(PickRejectHotkeys.Resolve(key), Is.EqualTo(expected));
  }

  [Test]
  public void BuildEdit_Pick_SetsPickAndClearsReject() {
    var edit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.Pick);

    Assert.Multiple(() => {
      Assert.That(edit.IsPick.HasValue, Is.True);
      Assert.That(edit.IsPick.Value, Is.True);
      Assert.That(edit.IsReject.HasValue, Is.True);
      Assert.That(edit.IsReject.Value, Is.Null);
    });
  }

  [Test]
  public void BuildEdit_Reject_SetsRejectAndClearsPick() {
    var edit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.Reject);

    Assert.Multiple(() => {
      Assert.That(edit.IsReject.HasValue, Is.True);
      Assert.That(edit.IsReject.Value, Is.True);
      Assert.That(edit.IsPick.HasValue, Is.True);
      Assert.That(edit.IsPick.Value, Is.Null);
    });
  }

  [Test]
  public void BuildEdit_Clear_ClearsBoth() {
    var edit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.Clear);

    Assert.Multiple(() => {
      Assert.That(edit.IsPick.HasValue, Is.True);
      Assert.That(edit.IsPick.Value, Is.Null);
      Assert.That(edit.IsReject.HasValue, Is.True);
      Assert.That(edit.IsReject.Value, Is.Null);
    });
  }

  [Test]
  public void BuildEdit_None_LeavesBothFieldsUntouched() {
    var edit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.None);

    Assert.Multiple(() => {
      Assert.That(edit.IsPick.HasValue, Is.False, "Untouched edit should not flag Pick");
      Assert.That(edit.IsReject.HasValue, Is.False, "Untouched edit should not flag Reject");
    });
  }

  [Test]
  public void BuildEdit_PickWriteAndReread_FlagSticks() {
    // End-to-end exercise of the edit produced by the hotkey through the
    // sidecar writer + reader.
    var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Hotkey-" + Guid.NewGuid().ToString("N")));
    dir.Create();
    try {
      var image = new FileInfo(Path.Combine(dir.FullName, "photo.jpg"));
      File.WriteAllBytes(image.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
      var writer = new XmpSidecarWriter();

      var pickEdit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.Pick);
      writer.ApplyAsync(image, pickEdit).GetAwaiter().GetResult();

      var reader = new MetadataReader();
      var afterPick = reader.ReadAsync(image).GetAwaiter().GetResult();
      Assert.That(afterPick.IsPick, Is.True);
      Assert.That(afterPick.IsReject, Is.Null);

      var clearEdit = PickRejectHotkeys.BuildEdit(PickRejectHotkeyAction.Clear);
      writer.ApplyAsync(image, clearEdit).GetAwaiter().GetResult();

      var afterClear = reader.ReadAsync(image).GetAwaiter().GetResult();
      Assert.That(afterClear.IsPick, Is.Null);
      Assert.That(afterClear.IsReject, Is.Null);
    } finally {
      if (dir.Exists)
        dir.Delete(recursive: true);
    }
  }
}
