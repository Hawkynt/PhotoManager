using PhotoManager.UI.Models;

namespace PhotoManager.UI.Services;

/// <summary>
/// Pure predicate that decides whether a row passes the pick/reject filter
/// chip. Lifted out of MainWindow so it's unit-testable without spinning up
/// an Avalonia control tree.
/// </summary>
public static class PickRejectFilter {
  public static bool Matches(bool isPick, bool isReject, PickRejectFilterMode mode) => mode switch {
    PickRejectFilterMode.ShowAll => true,
    PickRejectFilterMode.HideRejects => !isReject,
    PickRejectFilterMode.PicksOnly => isPick,
    PickRejectFilterMode.RejectsOnly => isReject,
    _ => true
  };

  public static bool Matches(FileItemModel item, PickRejectFilterMode mode)
    => Matches(item.IsPick, item.IsReject, mode);
}
