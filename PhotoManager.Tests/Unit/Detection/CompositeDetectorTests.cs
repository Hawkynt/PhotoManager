using Hawkynt.PhotoManager.Core.Detection;

namespace Hawkynt.PhotoManager.Tests.Unit.Detection;

[TestFixture]
public class CompositeDetectorTests {
  [Test]
  public async Task DetectAsync_PrimaryHasLabels_FallbackNotConsulted() {
    var primary = new StaticDetector("alpha");
    var fallback = new StaticDetector("beta");
    var composite = new CompositeDetector(primary, fallback);

    var result = await composite.DetectAsync(new FileInfo("x.jpg"));

    var names = result.DistinctLabelNames().ToArray();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("alpha"));
      Assert.That(names, Does.Not.Contain("beta"));
      Assert.That(fallback.CallCount, Is.Zero, "fallback should not run when primary returns labels");
    });
  }

  [Test]
  public async Task DetectAsync_PrimaryEmpty_FallsThroughToFallback() {
    var primary = new StaticDetector(); // no labels
    var fallback = new StaticDetector("beta");
    var composite = new CompositeDetector(primary, fallback);

    var result = await composite.DetectAsync(new FileInfo("x.jpg"));

    Assert.That(result.DistinctLabelNames(), Does.Contain("beta"));
    Assert.That(fallback.CallCount, Is.EqualTo(1));
  }

  [Test]
  public async Task DetectAsync_BothEmpty_ReturnsEmpty() {
    var composite = new CompositeDetector(new StaticDetector(), new StaticDetector());
    var result = await composite.DetectAsync(new FileInfo("x.jpg"));
    Assert.That(result.Labels, Is.Empty);
  }

  [Test]
  public void Ctor_NullArguments_Throw() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => new CompositeDetector(null!, new StaticDetector()));
      Assert.Throws<ArgumentNullException>(() => new CompositeDetector(new StaticDetector(), null!));
    });
  }

  private sealed class StaticDetector : IDetector {
    private readonly string[] _labels;
    public int CallCount { get; private set; }

    public StaticDetector(params string[] labels) => this._labels = labels;

    public Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
      this.CallCount++;
      var labels = this._labels
        .Select(l => new DetectionLabel(l, 1f, DetectionKind.Object))
        .ToArray();
      return Task.FromResult(new DetectionResult(labels));
    }
  }
}
