using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoManager.Core.Detection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Faces;

/// <summary>
/// Produces a fixed-length embedding vector for a face crop using an ONNX
/// model (MobileFaceNet or ArcFace-compatible). The embedding is what
/// <see cref="PeopleRegistry.FindMatch"/> uses to auto-recognize a face
/// against named references.
///
/// Expected model: input <c>[1, 3, 112, 112]</c> float32 NCHW, pixels
/// normalized to the [-1, 1] range (the MobileFaceNet convention). Output
/// is either a flat 512-D vector or a <c>[1, N]</c> tensor; we L2-normalize
/// the result so cosine similarity against the registry is well-defined.
///
/// Drop a compatible model at
/// <c>AppDataPaths.ModelFile("face-embedder.onnx")</c>. If missing, embedding
/// requests return null and the caller's registry match simply doesn't fire.
/// </summary>
public sealed class OnnxFaceEmbedder : IDisposable {
  public const string DefaultModelFileName = "face-embedder.onnx";
  private const int InputSize = 112;

  private readonly Lazy<InferenceSession?> _session;

  public OnnxFaceEmbedder(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Produces the embedding for a face cropped from <paramref name="imageFile"/>
  /// at the <paramref name="normalizedBox"/>. Returns null when the model
  /// isn't loaded, the file doesn't exist, or decoding fails.
  /// </summary>
  public async Task<float[]?> EmbedFaceAsync(
    FileInfo imageFile,
    NormalizedBoundingBox normalizedBox,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(imageFile);
    var session = this._session.Value;
    if (session == null || !imageFile.Exists)
      return null;

    return await Task.Run(() => this.RunInference(session, imageFile, normalizedBox), cancellationToken);
  }

  private float[]? RunInference(InferenceSession session, FileInfo imageFile, NormalizedBoundingBox normalizedBox) {
    Image<Rgba32> image;
    try {
      image = Image.Load<Rgba32>(imageFile.FullName);
    } catch {
      return null;
    }

    using var ownedImage = image;

    var faceCrop = CropNormalized(ownedImage, normalizedBox);
    if (faceCrop == null)
      return null;

    using var ownedCrop = faceCrop;
    var tensor = BuildInputTensor(ownedCrop);
    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, InputSize, InputSize });

    using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
    var output = results.First().AsTensor<float>().ToArray();
    return L2Normalize(output);
  }

  private static Image<Rgba32>? CropNormalized(Image<Rgba32> image, NormalizedBoundingBox box) {
    var x = (int)Math.Round(box.X * image.Width);
    var y = (int)Math.Round(box.Y * image.Height);
    var w = (int)Math.Round(box.Width * image.Width);
    var h = (int)Math.Round(box.Height * image.Height);

    x = Math.Clamp(x, 0, image.Width - 1);
    y = Math.Clamp(y, 0, image.Height - 1);
    w = Math.Clamp(w, 1, image.Width - x);
    h = Math.Clamp(h, 1, image.Height - y);

    var cropped = image.Clone(c => c.Crop(new Rectangle(x, y, w, h)).Resize(InputSize, InputSize));
    return cropped;
  }

  private static float[] BuildInputTensor(Image<Rgba32> face) {
    var tensor = new float[1 * 3 * InputSize * InputSize];
    var pixelCount = InputSize * InputSize;

    face.ProcessPixelRows(accessor => {
      for (var y = 0; y < InputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < InputSize; x++) {
          var px = row[x];
          var offset = y * InputSize + x;
          // MobileFaceNet convention: (pixel - 127.5) / 127.5 → [-1, 1].
          tensor[0 * pixelCount + offset] = (px.R - 127.5f) / 127.5f;
          tensor[1 * pixelCount + offset] = (px.G - 127.5f) / 127.5f;
          tensor[2 * pixelCount + offset] = (px.B - 127.5f) / 127.5f;
        }
      }
    });

    return tensor;
  }

  internal static float[] L2Normalize(float[] vector) {
    double sumOfSquares = 0;
    for (var i = 0; i < vector.Length; i++)
      sumOfSquares += vector[i] * vector[i];

    var magnitude = Math.Sqrt(sumOfSquares);
    if (magnitude <= 1e-9)
      return vector;  // all-zero vector — nothing to normalize

    var result = new float[vector.Length];
    for (var i = 0; i < vector.Length; i++)
      result[i] = (float)(vector[i] / magnitude);
    return result;
  }

  private static InferenceSession? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      return new InferenceSession(modelFile.FullName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    if (this._session.IsValueCreated)
      this._session.Value?.Dispose();
  }
}
