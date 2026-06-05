using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// Tracks a single long-running operation that should surface in the main
/// window status strip. <see cref="Fraction"/> is NaN for indeterminate work
/// so a callsite that doesn't know its total work up-front can still show
/// progress (the strip switches to indeterminate mode).
///
/// Mutable INPC by design: callers update <see cref="Description"/> and
/// <see cref="Fraction"/> as work proceeds and the bound view updates live.
/// </summary>
public sealed class OperationProgress : INotifyPropertyChanged {
  private bool _isRunning = true;
  private string _description = string.Empty;
  private double _fraction = double.NaN;

  public OperationProgress(string description, Action? cancel = null) {
    this._description = description;
    this.Cancel = cancel;
  }

  public bool IsRunning {
    get => this._isRunning;
    set => this.SetProperty(ref this._isRunning, value);
  }

  public string Description {
    get => this._description;
    set => this.SetProperty(ref this._description, value);
  }

  /// <summary>0..1 for determinate work, NaN for indeterminate.</summary>
  public double Fraction {
    get => this._fraction;
    set => this.SetProperty(ref this._fraction, value);
  }

  public Action? Cancel { get; }

  public bool IsIndeterminate => double.IsNaN(this._fraction);

  /// <summary>Convenience for ProgressBar.Value bindings (0..100, 0 when indeterminate).</summary>
  public double Percentage => double.IsNaN(this._fraction) ? 0 : Math.Clamp(this._fraction, 0, 1) * 100.0;

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    if (propertyName == nameof(this.Fraction)) {
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsIndeterminate)));
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Percentage)));
    }
  }
}
