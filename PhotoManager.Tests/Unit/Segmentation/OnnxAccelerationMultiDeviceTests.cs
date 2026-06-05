using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Core.Segmentation;

namespace Hawkynt.PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
[Category("Unit")]
public sealed class OnnxAccelerationMultiDeviceTests {
  [Test]
  public void CreateMultiDeviceSessions_returns_at_least_CPU_when_any_model_is_installed() {
    // Pick whichever small model is most likely installed locally.
    // Tests in CI without models skip cleanly via Inconclusive.
    var candidates = new[] {
      ModelRegistry.LamaInpaint,
      ModelRegistry.BopbScratchDetector,
      ModelRegistry.ColorizeDDColorPaperTiny
    };
    var available = candidates.FirstOrDefault(c => c.IsInstalled());
    if (available is null)
      Assert.Inconclusive("No small ONNX model installed locally — can't test session creation.");

    var modelPath = Hawkynt.PhotoManager.Core.AppDataPaths.ModelFile(available.FileName).FullName;
    var sessions = OnnxAcceleration.CreateMultiDeviceSessions(modelPath);

    Assert.That(sessions, Is.Not.Empty,
      "CreateMultiDeviceSessions must always return at least the CPU fallback session.");
    Assert.That(sessions.Any(s => s.Device.Equals("CPU", System.StringComparison.OrdinalIgnoreCase)),
      Is.True, "Expected CPU session to always be present as fallback.");
    TestContext.Out.WriteLine($"Created {sessions.Count} session(s) for {available.FileName}: " +
      string.Join(", ", sessions.Select(s => s.Device)));
  }

  [Test]
  public void CreateMultiDeviceSessions_caches_per_device_so_repeat_calls_return_same_instances() {
    var available = new[] {
      ModelRegistry.LamaInpaint,
      ModelRegistry.BopbScratchDetector,
      ModelRegistry.ColorizeDDColorPaperTiny
    }.FirstOrDefault(c => c.IsInstalled());
    if (available is null)
      Assert.Inconclusive("No model installed.");

    var modelPath = Hawkynt.PhotoManager.Core.AppDataPaths.ModelFile(available.FileName).FullName;
    var first = OnnxAcceleration.CreateMultiDeviceSessions(modelPath);
    var second = OnnxAcceleration.CreateMultiDeviceSessions(modelPath);

    Assert.That(second, Has.Count.EqualTo(first.Count));
    for (var i = 0; i < first.Count; i++) {
      Assert.That(ReferenceEquals(first[i].Session, second[i].Session), Is.True,
        $"Session for device '{first[i].Device}' should be the SAME cached instance across calls.");
    }
  }
}
