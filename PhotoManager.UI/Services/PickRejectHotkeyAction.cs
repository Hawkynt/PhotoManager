using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Services;

/// <summary>
/// What the P/X/U hotkeys do when bound to the file grid. Lifted out of the
/// MainWindow code-behind so a test can drive the same logic without an
/// Avalonia hosting environment.
/// </summary>
public enum PickRejectHotkeyAction {
  None,
  Pick,
  Reject,
  Clear
}

/// <summary>
/// Maps a key stroke to the cull action the file grid should perform. Returns
/// <see cref="PickRejectHotkeyAction.None"/> for keys that aren't bound — the
/// caller is responsible for then propagating the event normally.
/// </summary>
public static class PickRejectHotkeys {
  public static PickRejectHotkeyAction Resolve(char keyChar) => char.ToUpperInvariant(keyChar) switch {
    'P' => PickRejectHotkeyAction.Pick,
    'X' => PickRejectHotkeyAction.Reject,
    'U' => PickRejectHotkeyAction.Clear,
    _ => PickRejectHotkeyAction.None
  };

  /// <summary>
  /// Build the <see cref="MetadataEdit"/> that flags / unflags a file
  /// according to the requested action. Pick and Reject are mutually
  /// exclusive — toggling one explicitly clears the other.
  /// </summary>
  public static MetadataEdit BuildEdit(PickRejectHotkeyAction action) => action switch {
    PickRejectHotkeyAction.Pick => new MetadataEdit {
      IsPick = Optional<bool?>.Set(true),
      IsReject = Optional<bool?>.Set(null)
    },
    PickRejectHotkeyAction.Reject => new MetadataEdit {
      IsPick = Optional<bool?>.Set(null),
      IsReject = Optional<bool?>.Set(true)
    },
    PickRejectHotkeyAction.Clear => new MetadataEdit {
      IsPick = Optional<bool?>.Set(null),
      IsReject = Optional<bool?>.Set(null)
    },
    _ => new MetadataEdit()
  };
}
