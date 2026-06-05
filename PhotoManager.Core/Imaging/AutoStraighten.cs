using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Imaging;

/// <summary>
/// Detect the dominant horizontal / vertical edge angle in an image and
/// suggest a rotation that brings the horizon level. Uses a coarse Hough
/// vote over angles in [-15°, +15°] — phone shots are rarely off by
/// more than that and a wider sweep just costs CPU.
///
/// Inputs:
///   source — full-resolution image. We internally downscale to a working
///            edge size (~512 px on the long axis) for speed; the angle
///            estimate is scale-invariant.
///
/// Returns the recommended rotation in degrees, with the convention that
/// applying that angle (clockwise positive) makes the image level. When
/// no strong horizontal/vertical structure is found, returns 0.
///
/// Pure C#, no model.
/// </summary>
public static class AutoStraighten {
  /// <summary>Working edge map size — longer dimension downscales to this for the vote.</summary>
  public const int WorkingSize = 512;

  /// <summary>Angular search range each side of 0°. ±15° covers all real-world hand-held tilt.</summary>
  public const double MaxAngleDegrees = 15.0;

  /// <summary>Vote granularity. 0.25° = 121 bins across [-15, +15].</summary>
  public const double AngleStepDegrees = 0.25;

  /// <summary>
  /// Returns the suggested rotation in degrees (positive = clockwise);
  /// apply this rotation to the source to level the horizon. Returns 0
  /// when no dominant horizontal/vertical structure is detected.
  /// </summary>
  public static double EstimateRotation(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    var w = source.Width;
    var h = source.Height;
    if (w < 16 || h < 16)
      return 0;

    // Downscale for the vote — angle estimate is preserved.
    var scale = (double)WorkingSize / Math.Max(w, h);
    var workW = Math.Max(8, (int)Math.Round(w * scale));
    var workH = Math.Max(8, (int)Math.Round(h * scale));
    using var small = source.Clone(c => c.Resize(workW, workH));

    // Sobel-magnitude + direction per pixel (single pass).
    var lum = new byte[workW * workH];
    small.ProcessPixelRows(accessor => {
      for (var y = 0; y < workH; y++) {
        var row = accessor.GetRowSpan(y);
        var rowOff = y * workW;
        for (var x = 0; x < workW; x++) {
          var p = row[x];
          lum[rowOff + x] = (byte)((77 * p.R + 150 * p.G + 29 * p.B) >> 8);
        }
      }
    });

    var bins = (int)Math.Ceiling(MaxAngleDegrees * 2 / AngleStepDegrees) + 1;
    var votes = new double[bins];
    // For each strong-edge pixel, accumulate vote weight at the angle
    // closest to its gradient orientation. We're after horizontal /
    // vertical edges only, so we fold gradient angles into [-90°, +90°]
    // and keep only the part close to 0° or ±90°.
    for (var y = 1; y < workH - 1; y++) {
      var prev = (y - 1) * workW;
      var cur = y * workW;
      var next = (y + 1) * workW;
      for (var x = 1; x < workW - 1; x++) {
        // Sobel 3x3.
        var gx =
          -lum[prev + x - 1] + lum[prev + x + 1]
          -2 * lum[cur + x - 1] + 2 * lum[cur + x + 1]
          -lum[next + x - 1] + lum[next + x + 1];
        var gy =
          -lum[prev + x - 1] - 2 * lum[prev + x] - lum[prev + x + 1]
          + lum[next + x - 1] + 2 * lum[next + x] + lum[next + x + 1];
        var magSq = gx * gx + gy * gy;
        if (magSq < 400)  // ~|grad| < 20 — drop noise pixels but keep bilinear-rotated soft edges
          continue;
        // Edge ORIENTATION (90° rotated from gradient direction).
        var orient = Math.Atan2(gx, gy);  // radians, range (-π, π]
        // Fold into the range [-π/2, +π/2].
        if (orient > Math.PI / 2) orient -= Math.PI;
        if (orient < -Math.PI / 2) orient += Math.PI;
        var orientDeg = orient * 180.0 / Math.PI;

        // Horizontal edge (orient near 0°): vote for orientDeg as the
        // tilt the image should be rotated by to make it level.
        // Vertical edge (orient near ±90°): vote for orientDeg - 90° (or +90°)
        // so a 91° edge contributes a +1° tilt vote.
        double tilt;
        if (Math.Abs(orientDeg) <= MaxAngleDegrees) {
          tilt = orientDeg;
        } else if (Math.Abs(Math.Abs(orientDeg) - 90) <= MaxAngleDegrees) {
          tilt = orientDeg > 0 ? orientDeg - 90 : orientDeg + 90;
        } else {
          continue;  // Diagonal edge — doesn't help horizon detection.
        }

        var bin = (int)Math.Round((tilt + MaxAngleDegrees) / AngleStepDegrees);
        if (bin < 0 || bin >= bins) continue;
        votes[bin] += Math.Sqrt(magSq);
      }
    }

    // Pick the maximum-vote bin. If it isn't meaningfully higher than the
    // overall mean, treat it as "no dominant tilt" and return 0.
    var maxBin = 0;
    var maxVote = 0.0;
    double sumVotes = 0;
    for (var i = 0; i < bins; i++) {
      sumVotes += votes[i];
      if (votes[i] > maxVote) {
        maxVote = votes[i];
        maxBin = i;
      }
    }
    if (maxVote <= 0)
      return 0;  // No edges at all — uniform image, nothing to straighten.
    var meanVote = sumVotes / bins;
    if (maxVote < meanVote * 2.0)
      return 0;  // No clear winner — don't rotate.

    return maxBin * AngleStepDegrees - MaxAngleDegrees;
  }
}
