using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Detection;

/// <summary>
/// Builds the 1×3×640×640 float32 NCHW tensor YOLO v8 expects. Images are
/// letterbox-resized (preserving aspect ratio, padding with gray) and
/// normalized to [0, 1]. Returns both the tensor and the letterbox info so
/// the postprocessor can map bounding boxes back to original coordinates.
/// </summary>
internal static class YoloPreProcess {
  public const int InputSize = 640;
  private static readonly Rgba32 PadColor = new(114, 114, 114);

  public static (float[] Tensor, LetterboxInfo Letterbox) BuildInput(FileInfo imageFile) {
    using var image = Image.Load<Rgba32>(imageFile.FullName);
    return BuildInput(image);
  }

  public static (float[] Tensor, LetterboxInfo Letterbox) BuildInput(Image<Rgba32> image) {
    var originalWidth = image.Width;
    var originalHeight = image.Height;

    var scale = (float)InputSize / Math.Max(originalWidth, originalHeight);
    var resizedWidth = (int)Math.Round(originalWidth * scale);
    var resizedHeight = (int)Math.Round(originalHeight * scale);
    var padX = (InputSize - resizedWidth) / 2f;
    var padY = (InputSize - resizedHeight) / 2f;

    using var canvas = new Image<Rgba32>(InputSize, InputSize, PadColor);
    image.Mutate(c => c.Resize(resizedWidth, resizedHeight));
    canvas.Mutate(c => c.DrawImage(image, new Point((int)Math.Round(padX), (int)Math.Round(padY)), 1f));

    var tensor = new float[1 * 3 * InputSize * InputSize];
    var pixelCount = InputSize * InputSize;
    canvas.ProcessPixelRows(accessor => {
      for (var y = 0; y < InputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < InputSize; x++) {
          var pixel = row[x];
          var offset = y * InputSize + x;
          tensor[0 * pixelCount + offset] = pixel.R / 255f;
          tensor[1 * pixelCount + offset] = pixel.G / 255f;
          tensor[2 * pixelCount + offset] = pixel.B / 255f;
        }
      }
    });

    var letterbox = new LetterboxInfo(originalWidth, originalHeight, scale, padX, padY);
    return (tensor, letterbox);
  }
}
