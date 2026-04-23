namespace PhotoManager.Core.Models;

/// <summary>
/// Catalog of downloadable detection models. URLs point at stable, public
/// mirrors (ONNX Model Zoo on GitHub for UltraFace, HuggingFace for YOLOv8n
/// which the onnx-community organisation maintains). If a URL ever stops
/// working, the user-facing failure message includes the local path so they
/// can drop the file in manually.
/// </summary>
public static class ModelRegistry {
  /// <summary>YOLO v8 Nano ONNX — 80 COCO classes, ~12 MB.</summary>
  public static readonly ModelInfo YoloV8n = new(
    Name: "yolov8n",
    FileName: "yolov8n.onnx",
    DisplayName: "YOLOv8 Nano (object detection)",
    Description: "80 COCO object classes. ~12 MB. Pre-exported ONNX mirror hosted by the Hyuto/yolov8-onnxruntime-web demo repo (raw.githubusercontent.com — no LFS, no auth).",
    // Previously pointed at huggingface.co/onnx-community/yolov8n which
    // returns 401 Unauthorized from some networks/countries. This raw
    // GitHub mirror is an ordinary blob — no auth, no LFS, works everywhere.
    DownloadUrl: "https://raw.githubusercontent.com/Hyuto/yolov8-onnxruntime-web/master/public/model/yolov8n.onnx",
    ApproximateSizeBytes: 12_821_996
  );

  /// <summary>UltraFace RFB-320 ONNX — face detection at 320×240, ~1.3 MB.</summary>
  public static readonly ModelInfo UltraFaceRfb320 = new(
    Name: "ultraface-rfb",
    FileName: "face-detector.onnx",
    DisplayName: "UltraFace RFB-320 (face detection)",
    Description: "Lightweight face detector, 320×240 input. ~1.3 MB. From the ONNX Model Zoo.",
    // Direct LFS URL (skips the github.com → media.githubusercontent redirect,
    // some networks/proxies break redirects of octet-stream responses).
    DownloadUrl: "https://media.githubusercontent.com/media/onnx/models/main/validated/vision/body_analysis/ultraface/models/version-RFB-320.onnx",
    ApproximateSizeBytes: 1_270_727
  );

  /// <summary>ArcFace ResNet-100 — face embedding for recognition, 512-D output.</summary>
  public static readonly ModelInfo ArcFace = new(
    Name: "arcface",
    FileName: "face-embedder.onnx",
    DisplayName: "ArcFace (face embedding)",
    Description: "Produces 512-D face embeddings for recognition / clustering. ~249 MB. From the ONNX Model Zoo.",
    // Direct LFS URL — same rationale as UltraFace.
    DownloadUrl: "https://media.githubusercontent.com/media/onnx/models/main/validated/vision/body_analysis/arcface/model/arcfaceresnet100-8.onnx",
    ApproximateSizeBytes: 261_036_388
  );

  public static readonly IReadOnlyList<ModelInfo> All = [YoloV8n, UltraFaceRfb320, ArcFace];

  public static ModelInfo? FindByName(string name) =>
    All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
