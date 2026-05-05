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

  /// <summary>MODNet — lightweight portrait/subject alpha matting (~25 MB ONNX).</summary>
  public static readonly ModelInfo SubjectMaskMODNet = new(
    Name: "modnet-subject",
    FileName: "modnet_photographic_portrait_matting.onnx",
    DisplayName: "MODNet — Subject mask",
    Description: "Lightweight portrait/subject alpha matting for the AI Subject mask in develop. ~25 MB, ONNX, MIT licence.",
    // ZHKKKe/MODNet does not publish ONNX in its GitHub releases — the
    // canonical mirror is the Xenova/modnet HuggingFace repo, which
    // hosts the ONNX export of the official photographic-portrait-matting
    // checkpoint (input "input": 1x3xHxW dynamic, output "output": 1x1xHxW).
    DownloadUrl: "https://huggingface.co/Xenova/modnet/resolve/main/onnx/model.onnx?download=true",
    ApproximateSizeBytes: 25_900_000
  );

  /// <summary>NAFNet ONNX — AI image restoration (denoise / deblur), ~92 MB. Default denoise model.</summary>
  public static readonly ModelInfo NafnetSidd = new(
    Name: "nafnet",
    FileName: "denoise.onnx",
    DisplayName: "NAFNet — general restoration (default)",
    Description: "NAFNet image-restoration ONNX. Dynamic 1×3×H×W input in [0..1]. ~92 MB. Hosted on the OpenCV org HuggingFace repo (opencv/deblurring_nafnet) — works as a general denoiser / deblurrer.",
    // The original onnx-community/NAFNet-SIDD-width64 repo went private (401),
    // so we point at opencv/deblurring_nafnet which is the same NAFNet
    // architecture exported to ONNX (1×3×H×W dynamic float32 in [0..1]) and
    // is publicly downloadable.
    DownloadUrl: "https://huggingface.co/opencv/deblurring_nafnet/resolve/main/deblurring_nafnet_2025may.onnx",
    ApproximateSizeBytes: 91_736_251
  );

  /// <summary>SCUNet GAN ONNX — Swin-Conv-UNet for blind image restoration, ~87 MB.</summary>
  public static readonly ModelInfo ScuNetGan = new(
    Name: "scunet-gan",
    FileName: "denoise-scunet.onnx",
    DisplayName: "SCUNet GAN — despeckling / old scans",
    Description: "Swin-Conv-UNet trained for blind image restoration with GAN loss. ~87 MB. Stronger on JPEG artefacts, paper texture, dust, and faded prints — pick this for old scans / B&W restoration jobs. Hosted on the deepghs HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/deepghs/image_restoration/resolve/main/SCUNet-GAN.onnx",
    ApproximateSizeBytes: 91_226_400
  );

  /// <summary>NAFNet GoPro-trained ONNX — motion-blur restoration, ~262 MB.</summary>
  public static readonly ModelInfo NafnetGoPro = new(
    Name: "nafnet-gopro",
    FileName: "denoise-deblur.onnx",
    DisplayName: "NAFNet GoPro — motion deblur",
    Description: "NAFNet width-64 trained on the GoPro motion-blur dataset. ~262 MB. Specifically tuned to recover from camera shake / subject motion blur — cleaner than the default NAFNet on real-world handheld blur.",
    DownloadUrl: "https://huggingface.co/deepghs/image_restoration/resolve/main/NAFNet-GoPro-width64.onnx",
    ApproximateSizeBytes: 274_726_400
  );

  /// <summary>NAFNet SIDD-trained ONNX — gold-standard low-light denoiser, ~446 MB. Big.</summary>
  public static readonly ModelInfo NafnetSiddPure = new(
    Name: "nafnet-sidd",
    FileName: "denoise-sidd.onnx",
    DisplayName: "NAFNet SIDD — low-light denoise (446 MB)",
    Description: "NAFNet width-64 trained purely on the SIDD smartphone-image-denoising dataset. ~446 MB. Highest quality on high-ISO sensor noise; download size is ~5× the default — use only when low-light noise is the priority.",
    DownloadUrl: "https://huggingface.co/deepghs/image_restoration/resolve/main/NAFNet-SIDD-width64.onnx",
    ApproximateSizeBytes: 467_762_800
  );

  /// <summary>FBCNN colour ONNX — blind JPEG / DCT / wavelet artifact remover, ~120 MB.</summary>
  public static readonly ModelInfo ArtifactRemoverFbcnnColor = new(
    Name: "fbcnn-color",
    FileName: "fbcnn-color.onnx",
    DisplayName: "FBCNN colour — JPEG / artifact remover (default)",
    Description: "Flexible Blind CNN (Jiang et al. 2021) — single-pass blind restoration of JPEG / DCT / wavelet compression artifacts: ringing, blocking and halos around hard edges. Operates on the whole image at once, RGB float32 [0..1] in NCHW. ~120 MB. Mirror: huggingface.co/Hawkynt/photomanager-models.",
    DownloadUrl: "https://huggingface.co/Hawkynt/photomanager-models/resolve/main/fbcnn-color.onnx",
    ApproximateSizeBytes: 120_000_000
  );

  /// <summary>Real-ESRGAN x4 ONNX — 4× super-resolution upscaler, ~67 MB. Default model.</summary>
  public static readonly ModelInfo RealEsrganX4 = new(
    Name: "real-esrgan-x4",
    FileName: "upscale.onnx",
    DisplayName: "Real-ESRGAN ×4 plus — general (default)",
    Description: "Real-ESRGAN ×4 ONNX. Dynamic 1×3×H×W input in [0..1] with output 1×3×(4H)×(4W). ~67 MB. Best general-purpose photo upscaler. Hosted on the imgdesignart HuggingFace mirror.",
    // Original Xenova/realesrgan-x4plus repo started returning 401. The
    // imgdesignart mirror is publicly downloadable and follows the same
    // dynamic 1×3×H×W float32 convention. User can drop a custom file at
    // AppDataPaths.ModelFile("upscale.onnx") and the OnnxUpscaler will pick it up.
    DownloadUrl: "https://huggingface.co/imgdesignart/realesrgan-x4-onnx/resolve/main/onnx/model.onnx",
    ApproximateSizeBytes: 67_051_787
  );

  /// <summary>Real-ESRGAN x4plus 128 — fixed 128×128 input variant, ~67 MB.</summary>
  public static readonly ModelInfo RealEsrganX4Fixed128 = new(
    Name: "real-esrgan-x4-128",
    FileName: "upscale-128.onnx",
    DisplayName: "Real-ESRGAN ×4 plus — fixed 128 tiles",
    Description: "Real-ESRGAN ×4 plus, fixed 128×128 input → 512×512 output. ~67 MB. Same architecture as the default but the fixed tile size gives more deterministic memory use on long passes (16× / 64×). Hosted on the bukuroo HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/bukuroo/RealESRGAN-ONNX/resolve/main/real-esrgan-x4plus-128.onnx",
    ApproximateSizeBytes: 67_160_311
  );

  /// <summary>SwinIR x4 GAN ONNX — Swin transformer-based upscaler, ~61 MB.</summary>
  public static readonly ModelInfo SwinIrX4 = new(
    Name: "swin-ir-x4",
    FileName: "upscale-swinir.onnx",
    DisplayName: "SwinIR ×4 GAN — alt architecture",
    Description: "Swin-Transformer-based super-resolution (003_realSR_BSRGAN_DFO_s64w8_SwinIR-M_x4_GAN). ~61 MB. Different texture / detail character vs Real-ESRGAN — pick whichever you prefer for a given photo. Hosted on the rocca HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/rocca/swin-ir-onnx/resolve/main/003_realSR_BSRGAN_DFO_s64w8_SwinIR-M_x4_GAN.onnx",
    ApproximateSizeBytes: 61_316_800
  );

  /// <summary>Real-ESRGAN x4 plus anime 6B (lightweight) — anime/illustration upscaler, ~4 MB.</summary>
  public static readonly ModelInfo RealEsrganX4Anime = new(
    Name: "real-esrgan-x4-anime",
    FileName: "upscale-anime.onnx",
    DisplayName: "Real-ESRGAN ×4 anime — illustrations",
    Description: "Lightweight Real-ESRGAN ×4 anime variant (4-block, 32-feature). ~4 MB. Sharper line work + flat colour preservation that's ideal for anime / manga / illustrations and stylised graphics. Hosted on the xiongjie HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/xiongjie/lightweight-real-ESRGAN-anime/resolve/main/RealESRGAN_x4plus_anime_4B32F.onnx",
    ApproximateSizeBytes: 4_500_000
  );

  /// <summary>Waifu2x SwinUNet photo ×4 ONNX — older arch with different texture character, ~18 MB.</summary>
  public static readonly ModelInfo Waifu2xPhotoX4 = new(
    Name: "waifu2x-photo-x4",
    FileName: "upscale-waifu2x.onnx",
    DisplayName: "Waifu2x SwinUNet ×4 — alt texture",
    Description: "Waifu2x SwinUNet ×4 trained on photos. ~18 MB. Predates Real-ESRGAN; smoother result with less aggressive sharpening — preferred by some for portraits and landscapes. Hosted on the deepghs HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/deepghs/waifu2x_onnx/resolve/main/20250502/onnx_models/swin_unet/photo/scale4x.onnx",
    ApproximateSizeBytes: 18_905_000
  );

  /// <summary>EDSR ×2 ONNX — Enhanced Deep Super-Resolution, ~5 MB.</summary>
  public static readonly ModelInfo EdsrX2 = new(
    Name: "edsr-x2",
    FileName: "upscale-edsr.onnx",
    DisplayName: "EDSR ×2 — classic baseline",
    Description: "EDSR (Enhanced Deep Super-Resolution) ×2 ONNX. ~5 MB. Smaller, faster classic CNN super-resolution; less aggressive than Real-ESRGAN, useful as a baseline. Native factor 2× — chains to 4×/16×/64× in PhotoManager via repeated passes. Hosted on the rupeshs HuggingFace mirror.",
    DownloadUrl: "https://huggingface.co/rupeshs/edsr-onnx/resolve/main/edsr_onnxsim_2x.onnx",
    ApproximateSizeBytes: 5_400_000
  );

  // Multi-file ONNXes hard-code their external-data filename inside the
  // protobuf graph, so we MUST keep the upstream filename on disk.
  // Renaming the .onnx side-car would make ONNX Runtime fail to load the
  // session at all (it looks for the original name next to the .onnx).

  /// <summary>DeOldify Artistic ONNX — colorise B&amp;W photos. Multi-file ONNX (~244 MB external weights).</summary>
  public static readonly ModelInfo ColorizeDeOldifyArtistic = new(
    Name: "deoldify-artistic",
    FileName: "deoldify-artistic.onnx",
    DisplayName: "DeOldify Artistic — colorise B&W (default)",
    Description: "DeOldify Artistic ONNX — restores colour to grayscale photos with bold, painterly choices. Best for old scans / B&W historical photos. Multi-file model: 423 KB graph + 244 MB external weights, both downloaded automatically.",
    DownloadUrl: "https://huggingface.co/thookham/DeOldify/resolve/main/deoldify-artistic.onnx",
    ApproximateSizeBytes: 423_123,
    ExternalDataFiles: [
      new ExternalDataFile(
        FileName: "deoldify-artistic.onnx.data",
        DownloadUrl: "https://huggingface.co/thookham/DeOldify/resolve/main/deoldify-artistic.onnx.data",
        ApproximateSizeBytes: 255_262_720
      )
    ]
  );

  /// <summary>DDColor paper-tiny ONNX — state-of-the-art (ECCV 2023) colorizer, ~258 MB.</summary>
  public static readonly ModelInfo ColorizeDDColorPaperTiny = new(
    Name: "ddcolor-paper-tiny",
    FileName: "ddcolor-paper-tiny.onnx",
    DisplayName: "DDColor paper-tiny — state-of-the-art (recommended)",
    Description: "DDColor (ECCV 2023) — substantially better than DeOldify on portraits / landscapes / scenes. Predicts only Lab a/b chroma at 256×256, source's full-resolution L stays untouched → perfect detail preservation. ~258 MB. Mirrored from instant-high/DDColor-onnx's gdrive zip; original DDColor weights from piddnad/DDColor.",
    DownloadUrl: "https://huggingface.co/Hawkynt/photomanager-models/resolve/main/ddcolor-paper-tiny.onnx",
    ApproximateSizeBytes: 270_220_918
  );

  /// <summary>DDColor artistic ONNX — full-quality variant at 512×512, ~934 MB.</summary>
  public static readonly ModelInfo ColorizeDDColorArtistic = new(
    Name: "ddcolor-artistic",
    FileName: "ddcolor-artistic.onnx",
    DisplayName: "DDColor artistic — full quality (large)",
    Description: "DDColor artistic variant — same architecture as paper-tiny but trained at 512×512 with bolder colour choices. Higher chroma resolution = sharper colour edges around fine detail. ~934 MB. Mirrored from instant-high/DDColor-onnx's gdrive zip; original DDColor weights from piddnad/DDColor.",
    DownloadUrl: "https://huggingface.co/Hawkynt/photomanager-models/resolve/main/ddcolor-artistic.onnx",
    ApproximateSizeBytes: 979_727_590
  );

  /// <summary>DeOldify Stable ONNX — colorise B&amp;W photos with more conservative output. Multi-file ONNX (~833 MB external weights).</summary>
  public static readonly ModelInfo ColorizeDeOldifyStable = new(
    Name: "deoldify-stable",
    FileName: "deoldify-stable.onnx",
    DisplayName: "DeOldify Stable — colorise B&W (subdued)",
    Description: "DeOldify Stable ONNX — more conservative colour choices than Artistic. Good for portraits / family photos where overshooting hue is unwelcome. Multi-file model: 588 KB graph + 833 MB external weights. Big download.",
    DownloadUrl: "https://huggingface.co/thookham/DeOldify/resolve/main/deoldify-stable.onnx",
    ApproximateSizeBytes: 587_579,
    ExternalDataFiles: [
      new ExternalDataFile(
        FileName: "deoldify-stable.onnx.data",
        DownloadUrl: "https://huggingface.co/thookham/DeOldify/resolve/main/deoldify-stable.onnx.data",
        ApproximateSizeBytes: 873_332_736
      )
    ]
  );

  /// <summary>LaMa Inpainting ONNX — fills user-marked damaged / missing regions, ~88 MB.</summary>
  public static readonly ModelInfo LamaInpaint = new(
    Name: "lama-inpainting",
    FileName: "inpaint-lama.onnx",
    DisplayName: "LaMa Inpainting (default)",
    Description: "Large Mask Inpainting (LaMa) ONNX. Reconstructs damaged / missing regions of a photo from a brush-painted mask: scratches, dust, torn corners, mould patches. Operates on 512×512 tiles internally, so it works on any source size. ~88 MB.",
    DownloadUrl: "https://huggingface.co/opencv/inpainting_lama/resolve/main/inpainting_lama_2025jan.onnx",
    ApproximateSizeBytes: 92_323_456
  );

  /// <summary>BOPB scratch detector ONNX — Microsoft's purpose-built scratch / damage detector, ~144 MB.</summary>
  public static readonly ModelInfo BopbScratchDetector = new(
    Name: "bopb-scratch-detector",
    FileName: "scratch-detector-bopb.onnx",
    DisplayName: "BOPB scratch detector — neural-net (recommended)",
    Description: "UNet from Microsoft's 'Bringing Old Photos Back to Life' (Wan et al. 2020), purpose-trained to find scratches, dust, paper-fold tears, mould, and edge damage in old photos. Substantially better than the classical Frangi filter on faint scratches across textured backgrounds. Multi-file ONNX: ~250 KB graph + ~144 MB external weights, both downloaded automatically.",
    DownloadUrl: "https://huggingface.co/Hawkynt/photomanager-models/resolve/main/scratch-detector-bopb.onnx",
    ApproximateSizeBytes: 249_226,
    ExternalDataFiles: [
      new ExternalDataFile(
        FileName: "scratch-detector-bopb.onnx.data",
        DownloadUrl: "https://huggingface.co/Hawkynt/photomanager-models/resolve/main/scratch-detector-bopb.onnx.data",
        ApproximateSizeBytes: 150_536_192
      )
    ]
  );

  /// <summary>GFPGAN v1.4 ONNX — face-only restoration (deblur, scratches, sharpen), ~324 MB.</summary>
  public static readonly ModelInfo GfpGanV14 = new(
    Name: "gfpgan-v1-4",
    FileName: "face-restore-gfpgan.onnx",
    DisplayName: "GFPGAN v1.4 — face restoration (default)",
    Description: "GFPGAN v1.4 ONNX — restores faces in old / damaged photos: removes scratches across faces, deblurs features, sharpens eyes / hair / skin. Operates on 512×512 face crops; the restoration window auto-detects faces and feeds them through. ~324 MB.",
    DownloadUrl: "https://huggingface.co/Meeperomi/GFPGANv1.4-onnx/resolve/main/GFPGANv1.4.onnx",
    ApproximateSizeBytes: 340_256_686
  );

  /// <summary>SigLIP base patch16-224 vision encoder — produces image embeddings for F3 auto-keyword tagging.</summary>
  public static readonly ModelInfo SiglipVision = new(
    Name: "siglip-vision",
    FileName: "siglip-vision.onnx",
    DisplayName: "SigLIP base/16-224 (image embedding)",
    Description: "Vision tower from Google's SigLIP base/16 224×224 model. Produces image embeddings for auto-keyword tagging. ~150 MB. Mirror: huggingface.co/Xenova/siglip-base-patch16-224.",
    DownloadUrl: "https://huggingface.co/Xenova/siglip-base-patch16-224/resolve/main/onnx/vision_model.onnx",
    ApproximateSizeBytes: 154_000_000
  );

  /// <summary>SigLIP base patch16-224 text encoder — encodes vocabulary words for cosine matching.</summary>
  public static readonly ModelInfo SiglipText = new(
    Name: "siglip-text",
    FileName: "siglip-text.onnx",
    DisplayName: "SigLIP base/16-224 (text embedding)",
    Description: "Text tower from Google's SigLIP base/16 224×224 model. Encodes vocabulary words for cosine matching against image embeddings. ~120 MB. Mirror: huggingface.co/Xenova/siglip-base-patch16-224. Note: requires a SigLIP-compatible SentencePiece tokenizer at runtime — see OnnxClipTextEncoder.ITokenizer.",
    DownloadUrl: "https://huggingface.co/Xenova/siglip-base-patch16-224/resolve/main/onnx/text_model.onnx",
    ApproximateSizeBytes: 122_000_000
  );

  /// <summary>Every upscale model registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> Upscalers = [RealEsrganX4, RealEsrganX4Fixed128, SwinIrX4, RealEsrganX4Anime, Waifu2xPhotoX4, EdsrX2];

  /// <summary>Every denoise / restoration model registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> Denoisers = [NafnetSidd, ScuNetGan, NafnetGoPro, NafnetSiddPure];

  /// <summary>Every JPEG / compression-artifact remover registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> ArtifactRemovers = [ArtifactRemoverFbcnnColor];

  /// <summary>Every colorisation model registered with the app. UI dropdown order = this list's order. DDColor variants first because they materially out-perform DeOldify on real photos.</summary>
  public static readonly IReadOnlyList<ModelInfo> Colorizers = [ColorizeDDColorPaperTiny, ColorizeDDColorArtistic, ColorizeDeOldifyArtistic, ColorizeDeOldifyStable];

  /// <summary>Every face-restoration model registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> FaceRestorers = [GfpGanV14];

  /// <summary>Every scratch-detection model registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> ScratchDetectors = [BopbScratchDetector];

  /// <summary>Every inpainting model registered with the app. UI dropdown order = this list's order.</summary>
  public static readonly IReadOnlyList<ModelInfo> Inpainters = [LamaInpaint];

  public static readonly IReadOnlyList<ModelInfo> All = [YoloV8n, UltraFaceRfb320, ArcFace, SubjectMaskMODNet, NafnetSidd, ScuNetGan, NafnetGoPro, NafnetSiddPure, ArtifactRemoverFbcnnColor, RealEsrganX4, RealEsrganX4Fixed128, SwinIrX4, RealEsrganX4Anime, Waifu2xPhotoX4, EdsrX2, ColorizeDDColorPaperTiny, ColorizeDDColorArtistic, ColorizeDeOldifyArtistic, ColorizeDeOldifyStable, GfpGanV14, BopbScratchDetector, LamaInpaint, SiglipVision, SiglipText];

  public static ModelInfo? FindByName(string name) =>
    All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
