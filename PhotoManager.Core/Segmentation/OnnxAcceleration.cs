using Microsoft.ML.OnnxRuntime;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// Builds <see cref="SessionOptions"/> with the best execution provider
/// available on the current platform.
///
/// <para>The <c>Microsoft.ML.OnnxRuntime.DirectML</c> NuGet package on
/// Windows registers a DirectML EP that runs ONNX inference on any DX12
/// GPU (NVIDIA / AMD / Intel / integrated). For ESRGAN-class upscalers
/// the speedup is typically 5–30× over the CPU EP; the same applies to
/// every other ONNX stage we ship (denoise / colorize / face restore /
/// artifact remover / detectors / scratch / inpaint).</para>
///
/// <para>Linux and macOS keep the base <c>Microsoft.ML.OnnxRuntime</c>
/// package and run on the CPU EP. The DML provider isn't registered
/// there, so <see cref="SessionOptions.AppendExecutionProvider"/>
/// throws — we catch and silently continue so the CPU EP remains as the
/// session's only provider. CUDA / CoreML EPs would slot in here too,
/// behind their own try blocks, when we ever ship those native bits.</para>
/// </summary>
public static class OnnxAcceleration {
  static OnnxAcceleration() {
    // OpenVINO compiles each ONNX model per-device on first session
    // creation. For ESRGAN / NAFNet / DDColor that's typically 15-30
    // seconds on the NPU and the user sees a long stall before the
    // model is usable. Pointing OV_CACHE_DIR at a local folder makes
    // OpenVINO persist the compiled blobs to disk; subsequent sessions
    // for the same (model, device, ORT-version) tuple load from the
    // cache and finish in ~1 second.
    try {
      var cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoManager", "OpenVinoCache");
      System.IO.Directory.CreateDirectory(cacheDir);
      Environment.SetEnvironmentVariable("OV_CACHE_DIR", cacheDir);
    } catch {
      // Best-effort — if we can't write to LocalAppData we just live
      // with the warmup cost on every launch.
    }
  }

  /// <summary>
  /// Build a fresh <see cref="SessionOptions"/> with every available
  /// hardware execution provider appended in priority order:
  ///   OpenVINO (Intel NPU + iGPU + CPU)  →  QNN (Qualcomm NPU)
  ///       →  DirectML (any DX12 GPU)  →  CPU.
  /// ONNX Runtime picks the first EP capable of running each operator —
  /// so a single session naturally distributes work across NPU + GPU when
  /// both are present, and the CPU EP catches anything no accelerator
  /// supports.
  ///
  /// Each EP append is best-effort; an unregistered provider throws and
  /// we silently fall through. That keeps PhotoManager usable on every
  /// platform regardless of which optional NuGet packages
  /// (Microsoft.ML.OnnxRuntime.OpenVINO / .QNN) are referenced.
  /// </summary>
  public static SessionOptions BuildOptions(int dmlDeviceId = 0) {
    // OpenVINO 1.20 (Intel build) rejects "AUTO" as device_type with
    // "wrong configuration value" even though it accepts MULTI / HETERO
    // / GPU / NPU / CPU. The earlier default of "AUTO" therefore caused
    // every call site to silently fall back to CPU. We now try the
    // user-preferred device type first, then walk a fallback chain of
    // values OpenVINO definitely accepts. Each attempt builds a fresh
    // SessionOptions because AppendExecutionProvider_OpenVINO can leave
    // the options in a half-attached state when it raises.
    var preferred = Environment.GetEnvironmentVariable("PHOTOMANAGER_OPENVINO_DEVICE")
                 ?? "MULTI:NPU,GPU,CPU";
    foreach (var deviceType in new[] {
        preferred,
        "MULTI:NPU,GPU,CPU",
        "MULTI:GPU,CPU",
        "HETERO:NPU,GPU,CPU",
        "GPU",
        "CPU",
    }) {
      var opts = TryBuildOpenVino(deviceType);
      if (opts is null) continue;
      LastSelectedDevice = $"OpenVINO/{deviceType}";
      return opts;
    }

    // No OpenVINO available (Linux base ORT package, or a Windows build
    // pinned to DirectML). Try the typed DirectML method, then string-
    // based EPs, otherwise return plain options whose only EP is CPU.
    var legacyOpts = new SessionOptions();
    if (TryAppendDml(legacyOpts, dmlDeviceId)) {
      LastSelectedDevice = $"DirectML/device{dmlDeviceId}";
      return legacyOpts;
    }
    if (TryAppend(legacyOpts, "QNN", null)) {
      LastSelectedDevice = "QNN";
      return legacyOpts;
    }
    LastSelectedDevice = "CPU";
    return legacyOpts;
  }

  /// <summary>Last successfully-selected EP / device label.
  /// Updated every time <see cref="BuildOptions"/> is called. Useful
  /// for surfacing the actual acceleration path in diagnostics or the
  /// UI status bar.</summary>
  public static string LastSelectedDevice { get; private set; } = "(uninitialised)";

  /// <summary>Build an OpenVINO-only SessionOptions for the given
  /// device_type. Returns null when the device_type is rejected.</summary>
  private static SessionOptions? TryBuildOpenVino(string deviceType) {
    var options = new SessionOptions();
    if (TryAppendOpenVino(options, deviceType))
      return options;
    options.Dispose();
    return null;
  }

  /// <summary>Best-effort string-based append (works for QNN / SNPE / XNNPACK / AZURE only).</summary>
  private static bool TryAppend(SessionOptions options, string providerName, Dictionary<string, string>? providerOptions) {
    try {
      if (providerOptions is null)
        options.AppendExecutionProvider(providerName);
      else
        options.AppendExecutionProvider(providerName, providerOptions);
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>Append DirectML EP via reflection — invokes
  /// <c>SessionOptions.AppendExecutionProvider_DML(int deviceId)</c>
  /// when present (DirectML NuGet package). Returns false when the
  /// method doesn't exist (base CPU package, Linux/macOS).</summary>
  private static bool TryAppendDml(SessionOptions options, int deviceId) {
    try {
      var method = typeof(SessionOptions).GetMethod(
        "AppendExecutionProvider_DML",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
        binder: null, types: new[] { typeof(int) }, modifiers: null);
      if (method is null) return false;
      method.Invoke(options, new object[] { deviceId });
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>Append OpenVINO EP via reflection — only succeeds when the
  /// Intel OpenVINO NuGet package is referenced. <paramref name="deviceType"/>
  /// is OpenVINO's device-routing string. Defaults to "AUTO" which lets
  /// OpenVINO's smart router pick NPU / GPU / CPU per-op; users can
  /// override to "MULTI:NPU,GPU,CPU" (forced parallel dispatch),
  /// "HETERO:NPU,GPU,CPU" (per-layer routing), or any single device via
  /// the <c>PHOTOMANAGER_OPENVINO_DEVICE</c> env var.</summary>
  private static bool TryAppendOpenVino(SessionOptions options, string deviceType = "AUTO") {
    try {
      // The Intel OpenVINO build of ORT exposes
      //   AppendExecutionProvider_OpenVINO(string deviceId)
      // (a single-string overload — NOT a struct as the older docs
      // suggest). When OpenVINO isn't the active ORT build, the method
      // doesn't exist and we no-op.
      var method = typeof(SessionOptions).GetMethod(
        "AppendExecutionProvider_OpenVINO",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
        binder: null, types: new[] { typeof(string) }, modifiers: null);
      if (method is null) return false;
      method.Invoke(options, new object[] { deviceType });
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>
  /// List of execution providers ORT has registered at runtime — useful
  /// for diagnostics ("does my build actually have DML / OpenVINO?").
  /// On a Windows DirectML build with no other EPs, returns
  /// <c>["DmlExecutionProvider", "CPUExecutionProvider"]</c>.
  /// </summary>
  public static IReadOnlyList<string> GetAvailableProviders() {
    try {
      return OrtEnv.Instance().GetAvailableProviders().ToList();
    } catch {
      return Array.Empty<string>();
    }
  }

  /// <summary>
  /// Open (or reuse) an inference session against
  /// <paramref name="modelPath"/>. Sessions are cached per absolute
  /// path for the process's lifetime so repeated instantiations of the
  /// same wrapper (e.g. clicking "Auto-detect scratches" five times in
  /// a row) all share one already-compiled session — OpenVINO model
  /// compilation only happens on the very first use, not every time
  /// the UI re-creates the wrapper. Sessions can be disposed via
  /// <see cref="ResetCache"/>; otherwise they live until process exit.
  /// Returned <see cref="InferenceSession"/> instances are NOT owned
  /// by the caller — do not dispose them.
  /// </summary>
  public static InferenceSession CreateSession(string modelPath, bool preferCpu = false) {
    // Cache key includes the EP choice so two wrappers asking for the
    // same model with different acceleration preferences don't collide.
    var key = Path.GetFullPath(modelPath) + (preferCpu ? "|cpu" : "|accel");
    lock (_sessionCacheLock) {
      if (_sessionCache.TryGetValue(key, out var cached))
        return cached;
      var options = preferCpu ? new SessionOptions() : BuildOptions();
      try {
        var session = new InferenceSession(modelPath, options);
        _sessionCache[key] = session;
        if (preferCpu)
          LastSelectedDevice = "CPU (forced)";
        return session;
      } finally {
        options.Dispose();
      }
    }
  }

  /// <summary>
  /// Build a list of one session PER physical device the OpenVINO
  /// runtime can reach (NPU, GPU, then bare CPU). For tile-based
  /// workloads (upscaler, denoiser, etc.) the caller can run a
  /// producer/consumer queue across this list so a tile starts on
  /// whichever device finishes its previous tile first — work-
  /// stealing across heterogeneous accelerators.
  ///
  /// <para>Sessions are cached per (model, device) just like the
  /// single-session API, so subsequent calls return the same
  /// instances without re-loading model weights. Devices that aren't
  /// available (no NPU on the box, OpenVINO not installed) are
  /// silently dropped from the returned list. The CPU EP entry is
  /// always present as a fallback so the caller can rely on a
  /// non-empty result whenever the model file exists.</para>
  ///
  /// <para>Returned <see cref="InferenceSession"/> instances are NOT
  /// owned by the caller — they live in <see cref="_sessionCache"/>
  /// and persist for the process. Don't dispose them; use
  /// <see cref="ResetCache"/> if you need teardown.</para>
  /// </summary>
  public static IReadOnlyList<(string Device, InferenceSession Session)> CreateMultiDeviceSessions(string modelPath) {
    var fullPath = Path.GetFullPath(modelPath);
    var sessions = new List<(string, InferenceSession)>();
    // OpenVINO single-device sessions: each session bound to one
    // physical accelerator. Two parallel Run() calls across two
    // sessions on different devices truly run concurrently —
    // unlike one MULTI:NPU,GPU,CPU session whose internal load-
    // balancer often serialises against itself for back-to-back
    // requests.
    foreach (var device in new[] { "NPU", "GPU" }) {
      var session = TryCreateOpenVinoDeviceSession(fullPath, device);
      if (session != null)
        sessions.Add(($"OV-{device}", session));
    }
    // CPU is always available as a fallback. Add only if it isn't
    // already covered by an OpenVINO/CPU mapping (so the caller
    // doesn't get two CPU consumers competing for the same cores).
    var cpu = CreateSession(modelPath, preferCpu: true);
    sessions.Add(("CPU", cpu));
    return sessions;
  }

  /// <summary>Try to open an OpenVINO session targeting one specific
  /// device. Returns null when OpenVINO can't find/use that device on
  /// the current machine. Result is cached just like the single-
  /// session API.</summary>
  private static InferenceSession? TryCreateOpenVinoDeviceSession(string fullPath, string device) {
    var key = fullPath + "|ov:" + device;
    lock (_sessionCacheLock) {
      if (_sessionCache.TryGetValue(key, out var cached))
        return cached;
      var options = TryBuildOpenVino(device);
      if (options is null)
        return null;
      try {
        var session = new InferenceSession(fullPath, options);
        _sessionCache[key] = session;
        return session;
      } catch {
        return null;
      } finally {
        options.Dispose();
      }
    }
  }

  /// <summary>Dispose every cached session and clear the cache. Useful
  /// for tests and for "rebuild after model swap" flows.</summary>
  public static void ResetCache() {
    lock (_sessionCacheLock) {
      foreach (var s in _sessionCache.Values) s.Dispose();
      _sessionCache.Clear();
    }
  }

  private static readonly Dictionary<string, InferenceSession> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
  private static readonly object _sessionCacheLock = new();
}
