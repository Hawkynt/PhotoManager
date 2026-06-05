using Hawkynt.PhotoManager.Core.Geocoding;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class SunMoonArrowTests {
  private const double BaseLength = 80.0;

  [Test]
  public void Azimuth0_North_ArrowPointsUp() {
    // Azimuth 0 deg = North -> arrow should point up (DeltaY positive, DeltaX ~ 0).
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 0, altitudeDegrees: 0, BaseLength);

    Assert.Multiple(() => {
      Assert.That(arrow.IsVisible, Is.True);
      Assert.That(arrow.DeltaX, Is.EqualTo(0).Within(0.01), "North: DeltaX should be ~0");
      Assert.That(arrow.DeltaY, Is.EqualTo(BaseLength).Within(0.01), "North: DeltaY should be full length upward");
    });
  }

  [Test]
  public void Azimuth90_East_ArrowPointsRight() {
    // Azimuth 90 deg = East -> arrow should point right (DeltaX positive, DeltaY ~ 0).
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 90, altitudeDegrees: 0, BaseLength);

    Assert.Multiple(() => {
      Assert.That(arrow.IsVisible, Is.True);
      Assert.That(arrow.DeltaX, Is.EqualTo(BaseLength).Within(0.01), "East: DeltaX should be full length rightward");
      Assert.That(arrow.DeltaY, Is.EqualTo(0).Within(0.01), "East: DeltaY should be ~0");
    });
  }

  [Test]
  public void Azimuth180_South_ArrowPointsDown() {
    // Azimuth 180 deg = South -> arrow should point down (DeltaY negative, DeltaX ~ 0).
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 180, altitudeDegrees: 0, BaseLength);

    Assert.Multiple(() => {
      Assert.That(arrow.IsVisible, Is.True);
      Assert.That(arrow.DeltaX, Is.EqualTo(0).Within(0.01), "South: DeltaX should be ~0");
      Assert.That(arrow.DeltaY, Is.EqualTo(-BaseLength).Within(0.01), "South: DeltaY should be full length downward");
    });
  }

  [Test]
  public void Azimuth270_West_ArrowPointsLeft() {
    // Azimuth 270 deg = West -> arrow should point left (DeltaX negative, DeltaY ~ 0).
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 270, altitudeDegrees: 0, BaseLength);

    Assert.Multiple(() => {
      Assert.That(arrow.IsVisible, Is.True);
      Assert.That(arrow.DeltaX, Is.EqualTo(-BaseLength).Within(0.01), "West: DeltaX should be full length leftward");
      Assert.That(arrow.DeltaY, Is.EqualTo(0).Within(0.01), "West: DeltaY should be ~0");
    });
  }

  [Test]
  public void Altitude0_Horizon_FullLengthLine() {
    // Altitude 0 deg (body at horizon) -> line should be at full base length.
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 45, altitudeDegrees: 0, BaseLength);

    Assert.That(arrow.IsVisible, Is.True);
    var length = Math.Sqrt(arrow.DeltaX * arrow.DeltaX + arrow.DeltaY * arrow.DeltaY);
    Assert.That(length, Is.EqualTo(BaseLength).Within(0.01), "At horizon, line should be full length");
  }

  [Test]
  public void Altitude90_Zenith_ZeroLengthLine_NotVisible() {
    // Altitude 90 deg (body overhead / zenith) -> cos(90) = 0, no arrow.
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 45, altitudeDegrees: 90, BaseLength);

    Assert.That(arrow.IsVisible, Is.False, "At zenith, arrow should not be visible");
  }

  [Test]
  public void NegativeAltitude_BelowHorizon_NotVisible() {
    // Altitude < 0 -> body below horizon, arrow should be hidden.
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 120, altitudeDegrees: -10, BaseLength);

    Assert.That(arrow.IsVisible, Is.False, "Below horizon, arrow should not be visible");
  }

  [Test]
  public void Altitude45_LineScaledByCosine() {
    // cos(45 deg) ~ 0.7071, so length ~ 80 * 0.7071 ~ 56.57.
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 0, altitudeDegrees: 45, BaseLength);

    Assert.That(arrow.IsVisible, Is.True);
    var length = Math.Sqrt(arrow.DeltaX * arrow.DeltaX + arrow.DeltaY * arrow.DeltaY);
    var expected = BaseLength * Math.Cos(45.0 * Math.PI / 180.0);
    Assert.That(length, Is.EqualTo(expected).Within(0.01),
      "At 45 deg altitude, line should be cos(45) x base length");
  }

  [Test]
  public void Compute_ReturnsValidResults_ForTypicalPhotoScenario() {
    // Berlin summer afternoon -- both sun and moon should produce results
    // without exceptions.
    var utc = new DateTime(2024, 6, 21, 14, 0, 0, DateTimeKind.Utc);
    var (sun, moon) = SunMoonArrowComputer.Compute(52.52, 13.405, utc);

    Assert.Multiple(() => {
      Assert.That(sun, Is.Not.Null, "Sun arrow should be computed");
      Assert.That(moon, Is.Not.Null, "Moon arrow should be computed");
      Assert.That(sun.AzimuthDegrees, Is.InRange(0.0, 360.0), "Sun azimuth should be normalized");
      Assert.That(moon.AzimuthDegrees, Is.InRange(0.0, 360.0), "Moon azimuth should be normalized");
    });
  }

  [Test]
  public void ArrowData_PreservesAzimuthAndAltitude() {
    var arrow = SunMoonArrowComputer.BuildArrow(azimuthDegrees: 135, altitudeDegrees: 30, BaseLength);

    Assert.Multiple(() => {
      Assert.That(arrow.AzimuthDegrees, Is.EqualTo(135.0));
      Assert.That(arrow.AltitudeDegrees, Is.EqualTo(30.0));
    });
  }

  [Test]
  public void NoGpsOrDate_ComputeDoesNotCrash() {
    // Edge case: equator, epoch time -- should still return valid data without
    // throwing. The caller is responsible for not calling Compute when there's
    // no GPS or date, but the method itself must be safe.
    var utc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var (sun, moon) = SunMoonArrowComputer.Compute(0, 0, utc);

    Assert.Multiple(() => {
      Assert.That(sun, Is.Not.Null);
      Assert.That(moon, Is.Not.Null);
      Assert.That(sun.AzimuthDegrees, Is.InRange(0.0, 360.0));
      Assert.That(moon.AzimuthDegrees, Is.InRange(0.0, 360.0));
    });
  }
}
