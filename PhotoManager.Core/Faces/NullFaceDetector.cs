namespace Hawkynt.PhotoManager.Core.Faces;

/// <summary>
/// No-op detector — always returns an empty list. The default wiring when
/// an ONNX face-detection model has not been plugged in, so the rest of
/// the face pipeline (XMP read/write, registry, CLI tagging) stays
/// functional without a detector. Drop in an ONNX-backed implementation
/// later without touching the service or CLI.
/// </summary>
public sealed class NullFaceDetector : IFaceDetector {
  public Task<IReadOnlyList<DetectedFace>> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<DetectedFace>>(Array.Empty<DetectedFace>());
}
