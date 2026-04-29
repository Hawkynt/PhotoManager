namespace PhotoManager.Core.Develop;

/// <summary>
/// Which compare layout the editor preview is currently showing. Cycles
/// in the order declared (After → Split → Slider → After).
/// </summary>
public enum CompareMode {
  After,
  Split,
  Slider
}

/// <summary>
/// Pure-logic state machine that backs the compare-mode toggle in
/// <c>EditImageWindow</c>. UI-free so it can be unit-tested without
/// spinning up Avalonia.
/// </summary>
public sealed class CompareModeState {
  public CompareMode Mode { get; private set; }

  public CompareModeState(CompareMode initial = CompareMode.After) {
    this.Mode = initial;
  }

  /// <summary>Advance to the next mode in the cycle After → Split → Slider → After.</summary>
  public CompareMode Cycle() {
    this.Mode = this.Mode switch {
      CompareMode.After  => CompareMode.Split,
      CompareMode.Split  => CompareMode.Slider,
      CompareMode.Slider => CompareMode.After,
      _                  => CompareMode.After
    };
    return this.Mode;
  }

  /// <summary>Force the state to a specific mode. Idempotent when already in that mode.</summary>
  public void Set(CompareMode mode) {
    this.Mode = mode;
  }

  public bool IsAfterVisible    => this.Mode == CompareMode.After;
  public bool IsSplitVisible    => this.Mode == CompareMode.Split;
  public bool IsSliderVisible   => this.Mode == CompareMode.Slider;

  /// <summary>True when the baseline render is needed (i.e. either compare layout is active).</summary>
  public bool NeedsBaseline => this.Mode != CompareMode.After;

  /// <summary>Toolbar-button label that reflects the active mode.</summary>
  public string ButtonContent => this.Mode switch {
    CompareMode.After  => "🟢 After only",
    CompareMode.Split  => "🔀 Split",
    CompareMode.Slider => "↔ Slider",
    _                  => "🟢 After only"
  };

  /// <summary>Tooltip text describing what the next click will do.</summary>
  public string ButtonTooltip => this.Mode switch {
    CompareMode.After  => "Click to compare against baseline (Split view)",
    CompareMode.Split  => "Click to switch to Slider (before/after wipe)",
    CompareMode.Slider => "Click to return to After-only view",
    _                  => "Cycle compare mode"
  };
}
