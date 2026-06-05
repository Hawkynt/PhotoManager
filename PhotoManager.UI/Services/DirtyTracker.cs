using Avalonia.Controls;

namespace Hawkynt.PhotoManager.UI.Services;

/// <summary>
/// Tracks edits on a set of input controls and paints their background
/// light-blue (via the <c>dirty</c> style class) whenever the current value
/// differs from the snapshot taken at the last <see cref="SetClean"/>. Used
/// by the Metadata panel + Properties dialog so unsaved edits are visually
/// obvious — the tint clears when Save / Revert / file-selection-change
/// reestablishes a clean baseline.
///
/// Supported control types: <see cref="TextBox"/>, <see cref="ComboBox"/>,
/// <see cref="CheckBox"/>, <see cref="DatePicker"/>, <see cref="TimePicker"/>.
/// </summary>
public sealed class DirtyTracker {
  private const string DirtyClass = "dirty";
  private readonly Dictionary<Control, object?> _cleanValues = new();
  private bool _suppressEvaluate;

  public void Track(Control control) {
    if (control is null)
      return;

    // Seed clean value so an Evaluate() right after Track() reports false.
    try {
      this._cleanValues[control] = GetCurrentValue(control);
    } catch {
      // Non-standard control type that our switch doesn't know — skip it
      // rather than letting a bad registration poison the whole tracker.
      return;
    }

    try {
      switch (control) {
        case TextBox tb:
          tb.TextChanged += (_, _) => this.EvaluateSafe(tb);
          break;
        case ComboBox cb:
          cb.SelectionChanged += (_, _) => this.EvaluateSafe(cb);
          break;
        // CheckBox is a ToggleButton, but match it explicitly so any future
        // CheckBox-specific signal could be wired here. Both surface
        // IsCheckedChanged on ToggleButton.
        case CheckBox chk:
          chk.IsCheckedChanged += (_, _) => this.EvaluateSafe(chk);
          break;
        case Avalonia.Controls.Primitives.ToggleButton tgl:
          tgl.IsCheckedChanged += (_, _) => this.EvaluateSafe(tgl);
          break;
        case DatePicker dp:
          dp.SelectedDateChanged += (_, _) => this.EvaluateSafe(dp);
          break;
        case TimePicker tp:
          tp.SelectedTimeChanged += (_, _) => this.EvaluateSafe(tp);
          break;
      }
    } catch {
      // Event subscription failed (version / theme mismatch). Leave the
      // clean value snapshot in place but skip tracking — worst case the
      // field won't light up blue when edited.
    }
  }

  private void EvaluateSafe(Control control) {
    try {
      this.Evaluate(control);
    } catch {
      // Class toggling from inside a change handler is best-effort — a
      // single theme / template glitch shouldn't abort image loading.
    }
  }

  /// <summary>
  /// Snapshot the current value of every tracked control as the new
  /// "clean" baseline and clear any dirty highlighting. Call after initial
  /// load, after a successful Save, and after Revert / file-switch so the
  /// visual state matches the persisted state.
  /// </summary>
  /// <summary>
  /// True if the control's current value differs from its last clean
  /// snapshot. Used by <c>BuildEditFromUi</c> in MainWindow so Save /
  /// Apply-to-selection only emit Optional values for fields the user
  /// actually changed — avoids stomping unrelated metadata on the other
  /// files in a multi-select.
  /// </summary>
  public bool IsDirty(Control? control) {
    if (control is null)
      return false;
    if (!this._cleanValues.TryGetValue(control, out var clean))
      return false;
    try {
      return !ValuesEqual(GetCurrentValue(control), clean);
    } catch {
      return false;
    }
  }

  public void SetClean() {
    this._suppressEvaluate = true;
    try {
      foreach (var control in this._cleanValues.Keys.ToList()) {
        try {
          this._cleanValues[control] = GetCurrentValue(control);
          control.Classes.Remove(DirtyClass);
        } catch {
          // One flaky control (mid-template, disposed, etc.) shouldn't
          // prevent the rest of the panel from resetting.
        }
      }
    } finally {
      this._suppressEvaluate = false;
    }
  }

  private void Evaluate(Control control) {
    if (this._suppressEvaluate)
      return;
    if (!this._cleanValues.TryGetValue(control, out var clean))
      return;

    var current = GetCurrentValue(control);
    var dirty = !ValuesEqual(current, clean);
    if (dirty) {
      if (!control.Classes.Contains(DirtyClass))
        control.Classes.Add(DirtyClass);
    } else {
      control.Classes.Remove(DirtyClass);
    }
  }

  private static object? GetCurrentValue(Control control) => control switch {
    TextBox tb => tb.Text ?? string.Empty,
    ComboBox cb => cb.SelectedItem,
    CheckBox chk => chk.IsChecked,
    Avalonia.Controls.Primitives.ToggleButton tgl => tgl.IsChecked,
    DatePicker dp => dp.SelectedDate,
    TimePicker tp => tp.SelectedTime,
    _ => null
  };

  private static bool ValuesEqual(object? a, object? b) {
    // Treat null and empty string as equivalent so a field that loaded
    // empty doesn't go "dirty" on every focus/blur dance.
    if (a is string sa && b is string sb)
      return string.Equals(sa, sb, StringComparison.Ordinal);
    if (a is null && b is string sbe) return sbe.Length == 0;
    if (b is null && a is string sae) return sae.Length == 0;
    return Equals(a, b);
  }
}
