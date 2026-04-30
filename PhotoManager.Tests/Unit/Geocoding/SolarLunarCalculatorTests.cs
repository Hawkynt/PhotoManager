using PhotoManager.Core.Geocoding;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class SolarLunarCalculatorTests {
  // Berlin solar noon on the summer solstice 2024-06-21. Solar noon there
  // happens around 11:13 UTC; reference values from NOAA's online solar
  // calculator: altitude ~60.95°, azimuth ~180° (due south). The simplified
  // (truncated-Meeus) algorithm is good to better than 0.5° on altitude
  // year-round; the azimuth is more sensitive to small time-of-day errors
  // because the sun moves ~15°/h along the horizon, so we allow a few
  // degrees there.
  [Test]
  public void Sun_BerlinSolstice_NoonAltitudeApprox61DegSouth() {
    var utc = new DateTime(2024, 6, 21, 11, 13, 0, DateTimeKind.Utc);
    var (azimuth, altitude) = SolarLunarCalculator.SunPosition(utc, 52.52, 13.405);
    Assert.Multiple(() => {
      Assert.That(altitude, Is.EqualTo(60.95).Within(0.5),
        $"altitude (deg) was {altitude:F2}");
      Assert.That(azimuth, Is.EqualTo(180.0).Within(3.0),
        $"azimuth (deg from N) was {azimuth:F2}");
    });
  }

  [Test]
  public void Sun_BelowHorizonAtMidnight_NorthernSummer() {
    // Berlin midnight UTC, summer — sun should be below the horizon.
    var utc = new DateTime(2024, 6, 21, 0, 0, 0, DateTimeKind.Utc);
    var (_, altitude) = SolarLunarCalculator.SunPosition(utc, 52.52, 13.405);
    Assert.That(altitude, Is.LessThan(0));
  }

  [Test]
  public void Sun_EquatorEquinoxNoon_AltitudeNear90() {
    // Equinox + equator + local noon (UTC 12 at lon 0) → sun overhead.
    var utc = new DateTime(2024, 3, 20, 12, 0, 0, DateTimeKind.Utc);
    var (_, altitude) = SolarLunarCalculator.SunPosition(utc, 0, 0);
    Assert.That(altitude, Is.EqualTo(90.0).Within(2.0));
  }

  [Test]
  public void Sun_AzimuthNormalizedTo0_360() {
    var utc = new DateTime(2024, 6, 21, 6, 0, 0, DateTimeKind.Utc);
    var (azimuth, _) = SolarLunarCalculator.SunPosition(utc, 52.52, 13.405);
    Assert.That(azimuth, Is.GreaterThanOrEqualTo(0));
    Assert.That(azimuth, Is.LessThan(360));
  }

  [Test]
  public void Moon_AltitudeStaysWithinPlausibleRange() {
    // Moon altitude must always be in [-90, 90].
    var utc = new DateTime(2024, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    var (azimuth, altitude) = SolarLunarCalculator.MoonPosition(utc, 52.52, 13.405);
    Assert.Multiple(() => {
      Assert.That(altitude, Is.InRange(-90.0, 90.0));
      Assert.That(azimuth, Is.InRange(0.0, 360.0));
    });
  }

  [Test]
  public void DescribeTwilight_BoundariesMatchExpectedRegimes() {
    Assert.Multiple(() => {
      Assert.That(SolarLunarCalculator.DescribeTwilight(45),  Is.EqualTo("Daylight"));
      Assert.That(SolarLunarCalculator.DescribeTwilight(3),   Is.EqualTo("Golden hour (sun low, warm light)"));
      Assert.That(SolarLunarCalculator.DescribeTwilight(-3),  Is.EqualTo("Blue hour / civil twilight"));
      Assert.That(SolarLunarCalculator.DescribeTwilight(-9),  Is.EqualTo("Nautical twilight"));
      Assert.That(SolarLunarCalculator.DescribeTwilight(-15), Is.EqualTo("Astronomical twilight"));
      Assert.That(SolarLunarCalculator.DescribeTwilight(-30), Is.EqualTo("Night"));
    });
  }

  [Test]
  public void Sun_HandlesLocalDateTimeKind_BySwitchingToUtc() {
    var utc = new DateTime(2024, 6, 21, 11, 13, 0, DateTimeKind.Utc);
    var asUnspecified = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    var local = utc.ToLocalTime();
    var (_, altUtc) = SolarLunarCalculator.SunPosition(utc, 52.52, 13.405);
    var (_, altLocal) = SolarLunarCalculator.SunPosition(local, 52.52, 13.405);
    var (_, altUnspecified) = SolarLunarCalculator.SunPosition(asUnspecified, 52.52, 13.405);
    Assert.That(altLocal, Is.EqualTo(altUtc).Within(0.001));
    Assert.That(altUnspecified, Is.EqualTo(altUtc).Within(0.001));
  }
}
