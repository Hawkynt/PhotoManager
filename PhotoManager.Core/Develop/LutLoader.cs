using System.Globalization;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Parses Adobe <c>.cube</c> and Autodesk <c>.3dl</c> 3D LUT files into a
/// <see cref="Lut3D"/>. Both formats use the same N³ RGB-triplet payload —
/// only the header preamble differs:
///
/// <list type="bullet">
///   <item><description><c>.cube</c>: <c>LUT_3D_SIZE N</c>, optional
///   <c>DOMAIN_MIN/MAX</c>, optional <c>TITLE</c>, then triplets in 0..1.</description></item>
///   <item><description><c>.3dl</c>: integer triplets scaled to a power-of-two
///   max value (e.g. 0..1023). Cube size is inferred from the row count
///   when no <c>Mesh</c> directive is present.</description></item>
/// </list>
///
/// Comments start with <c>#</c>; blank lines are ignored. Both Unix and
/// Windows newlines work since we tokenise on whitespace.
/// </summary>
public static class LutLoader {
  public static Lut3D Load(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      throw new FileNotFoundException("LUT file not found.", file.FullName);
    return file.Extension.Equals(".3dl", StringComparison.OrdinalIgnoreCase)
      ? Parse3dl(File.ReadAllText(file.FullName))
      : ParseCube(File.ReadAllText(file.FullName));
  }

  public static Lut3D ParseCube(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var inv = CultureInfo.InvariantCulture;
    int? size = null;
    var domainMin = new[] { 0f, 0f, 0f };
    var domainMax = new[] { 1f, 1f, 1f };
    var samples = new List<float>();

    foreach (var raw in text.Split('\n')) {
      var line = StripComment(raw).Trim();
      if (line.Length == 0)
        continue;

      var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (tokens.Length == 0)
        continue;

      var head = tokens[0];

      if (head.Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) {
        if (tokens.Length < 2 || !int.TryParse(tokens[1], NumberStyles.Integer, inv, out var n) || n < 2)
          throw new FormatException("Invalid LUT_3D_SIZE header.");
        size = n;
        continue;
      }

      if (head.Equals("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
        throw new FormatException("1D LUTs are not supported — expected LUT_3D_SIZE.");

      if (head.Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase)) {
        ReadTriplet(tokens, 1, inv, domainMin);
        continue;
      }

      if (head.Equals("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase)) {
        ReadTriplet(tokens, 1, inv, domainMax);
        continue;
      }

      if (head.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
        continue;

      // Anything else with three numeric tokens is a sample row.
      if (tokens.Length < 3)
        throw new FormatException($"Unexpected LUT line: '{line}'.");
      if (!float.TryParse(tokens[0], NumberStyles.Float, inv, out var r)
       || !float.TryParse(tokens[1], NumberStyles.Float, inv, out var g)
       || !float.TryParse(tokens[2], NumberStyles.Float, inv, out var b))
        throw new FormatException($"Unexpected LUT line: '{line}'.");

      samples.Add(r);
      samples.Add(g);
      samples.Add(b);
    }

    if (size is not int n3)
      throw new FormatException("Missing LUT_3D_SIZE header.");
    var expected = n3 * n3 * n3 * 3;
    if (samples.Count != expected)
      throw new FormatException($"LUT sample count mismatch: expected {expected}, got {samples.Count}.");

    var table = samples.ToArray();
    NormaliseDomain(table, domainMin, domainMax);
    return new(n3, table);
  }

  public static Lut3D Parse3dl(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var inv = CultureInfo.InvariantCulture;
    int? meshExp = null;
    int? declaredSize = null;
    var rawTriplets = new List<int[]>();
    var maxValueSeen = 0;

    foreach (var raw in text.Split('\n')) {
      var line = StripComment(raw).Trim();
      if (line.Length == 0)
        continue;

      var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (tokens.Length == 0)
        continue;

      if (tokens[0].Equals("Mesh", StringComparison.OrdinalIgnoreCase)) {
        if (tokens.Length < 3
         || !int.TryParse(tokens[1], NumberStyles.Integer, inv, out var inExp)
         || !int.TryParse(tokens[2], NumberStyles.Integer, inv, out var _outExp))
          throw new FormatException("Invalid Mesh directive in .3dl.");
        meshExp = inExp;
        continue;
      }

      if (tokens[0].Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) {
        if (tokens.Length < 2 || !int.TryParse(tokens[1], NumberStyles.Integer, inv, out var n) || n < 2)
          throw new FormatException("Invalid LUT_3D_SIZE header in .3dl.");
        declaredSize = n;
        continue;
      }

      // .3dl files commonly start with a "shaper" row of indexes before the
      // sample block — skip until we see something that looks like a triplet.
      if (tokens.Length == 3
        && int.TryParse(tokens[0], NumberStyles.Integer, inv, out var ri)
        && int.TryParse(tokens[1], NumberStyles.Integer, inv, out var gi)
        && int.TryParse(tokens[2], NumberStyles.Integer, inv, out var bi)) {
        rawTriplets.Add(new[] { ri, gi, bi });
        if (ri > maxValueSeen) maxValueSeen = ri;
        if (gi > maxValueSeen) maxValueSeen = gi;
        if (bi > maxValueSeen) maxValueSeen = bi;
        continue;
      }
      // Lines with more than three integers are shaper-table rows — ignore.
      if (tokens.All(t => int.TryParse(t, NumberStyles.Integer, inv, out _)))
        continue;
      throw new FormatException($"Unexpected .3dl line: '{line}'.");
    }

    var totalSamples = rawTriplets.Count;
    int size;
    if (declaredSize is int explicitSize)
      size = explicitSize;
    else if (meshExp is int exp)
      size = (int)Math.Round(Math.Pow(2, exp) + 1);
    else
      size = (int)Math.Round(Math.Cbrt(totalSamples));
    if (size < 2 || size * size * size != totalSamples)
      throw new FormatException($".3dl sample count {totalSamples} doesn't match a cube size.");

    // Choose a sensible normaliser: largest sample rounded up to the next
    // power-of-two minus 1 (so 0..1023 → divide by 1023, 0..4095 → /4095).
    var divisor = NextPowerOfTwoMinusOne(maxValueSeen);
    if (divisor <= 0)
      divisor = 1;

    var table = new float[totalSamples * 3];
    for (var i = 0; i < totalSamples; i++) {
      var t = rawTriplets[i];
      table[i * 3]     = t[0] / (float)divisor;
      table[i * 3 + 1] = t[1] / (float)divisor;
      table[i * 3 + 2] = t[2] / (float)divisor;
    }
    return new(size, table);
  }

  private static string StripComment(string line) {
    var hash = line.IndexOf('#');
    return hash < 0 ? line : line[..hash];
  }

  private static void ReadTriplet(string[] tokens, int offset, IFormatProvider inv, float[] target) {
    if (tokens.Length < offset + 3)
      throw new FormatException("Expected three values for DOMAIN triplet.");
    for (var i = 0; i < 3; i++) {
      if (!float.TryParse(tokens[offset + i], NumberStyles.Float, inv, out var v))
        throw new FormatException("Invalid DOMAIN value.");
      target[i] = v;
    }
  }

  private static void NormaliseDomain(float[] table, float[] domainMin, float[] domainMax) {
    var rRange = domainMax[0] - domainMin[0];
    var gRange = domainMax[1] - domainMin[1];
    var bRange = domainMax[2] - domainMin[2];
    if (Math.Abs(rRange) < 1e-9f) rRange = 1f;
    if (Math.Abs(gRange) < 1e-9f) gRange = 1f;
    if (Math.Abs(bRange) < 1e-9f) bRange = 1f;
    if (Math.Abs(domainMin[0]) < 1e-6f && Math.Abs(domainMin[1]) < 1e-6f && Math.Abs(domainMin[2]) < 1e-6f
      && Math.Abs(rRange - 1f) < 1e-6f && Math.Abs(gRange - 1f) < 1e-6f && Math.Abs(bRange - 1f) < 1e-6f)
      return;

    for (var i = 0; i < table.Length; i += 3) {
      table[i]     = (table[i]     - domainMin[0]) / rRange;
      table[i + 1] = (table[i + 1] - domainMin[1]) / gRange;
      table[i + 2] = (table[i + 2] - domainMin[2]) / bRange;
    }
  }

  private static int NextPowerOfTwoMinusOne(int max) {
    if (max <= 0)
      return 0;
    var v = 1;
    while (v - 1 < max)
      v <<= 1;
    return v - 1;
  }
}
