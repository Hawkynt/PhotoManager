namespace Hawkynt.PhotoManager.Core.Detection;

/// <summary>
/// Pluggable entry point for any detector — ONNX models, cloud services, or
/// rule-based heuristics. The detection pipeline (auto-keyword writing,
/// face region storage) is agnostic to which implementation is running.
/// </summary>
public interface IDetector {
  Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default);
}
