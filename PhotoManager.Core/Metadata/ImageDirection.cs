namespace Hawkynt.PhotoManager.Core.Metadata;

/// <summary>
/// Direction the camera was pointing when the photo was taken.
/// <see cref="Degrees"/> is in the 0..360 range (0 = north, clockwise).
/// <see cref="Reference"/> distinguishes true-north ("T") from magnetic-north
/// ("M"); GPS devices without a compass usually omit the reference, in which
/// case callers can treat it as true-north.
/// </summary>
public readonly record struct ImageDirection(double Degrees, DirectionReference Reference = DirectionReference.True) {
  public bool IsValid => this.Degrees is >= 0 and <= 360;

  public string ReferenceTag => this.Reference switch {
    DirectionReference.Magnetic => "M",
    _ => "T"
  };

  public static DirectionReference ParseReference(string? tag) => tag?.ToUpperInvariant() switch {
    "M" => DirectionReference.Magnetic,
    _ => DirectionReference.True
  };
}

public enum DirectionReference {
  True,
  Magnetic
}
