using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Hawkynt.PhotoManager.Core.Video;

/// <summary>
/// Drives ffmpeg as a subprocess to crack a video into a sequence of
/// JPEG frames suitable for the panorama stitcher. ffmpeg is detected
/// via <see cref="FfmpegLocator"/>; missing binary throws so callers
/// can show install instructions.
/// </summary>
public sealed class VideoFrameExtractor {
  private static readonly Regex FrameProgressRegex =
    new(@"frame=\s*(\d+)", RegexOptions.Compiled);

  private readonly FileInfo? _ffmpegOverride;

  public VideoFrameExtractor() : this(null) { }

  public VideoFrameExtractor(FileInfo? ffmpegOverride) {
    this._ffmpegOverride = ffmpegOverride;
  }

  /// <summary>
  /// Returns the argument list (excluding the executable itself) that
  /// would be passed to ffmpeg for the given inputs. Pure / side-effect
  /// free so unit tests can pin the wire format.
  /// </summary>
  public static IReadOnlyList<string> BuildCommandLine(
    FileInfo videoFile,
    DirectoryInfo outputDir,
    VideoExtractOptions options
  ) {
    var inv = CultureInfo.InvariantCulture;
    var args = new List<string>();

    if (options.StartTime is { } start) {
      args.Add("-ss");
      args.Add(FormatTimeSpan(start));
    }
    if (options.EndTime is { } end) {
      args.Add("-to");
      args.Add(FormatTimeSpan(end));
    }

    args.Add("-i");
    args.Add(videoFile.FullName);

    var fps = options.Fps.ToString("0.###", inv);
    var filter = $"fps={fps}";
    if (options.MaxLongEdge is { } maxEdge && maxEdge > 0) {
      // scale=if(gt(iw,ih),min(maxEdge,iw),-2):if(gt(iw,ih),-2,min(maxEdge,ih))
      // — keeps aspect ratio, never upscales, force-rounds to even
      // dimensions (jpeg-safe).
      var m = maxEdge.ToString(inv);
      filter += $",scale='if(gt(iw,ih),min({m},iw),-2)':'if(gt(iw,ih),-2,min({m},ih))'";
    }
    args.Add("-vf");
    args.Add(filter);

    args.Add("-q:v");
    args.Add(options.JpegQuality.ToString(inv));

    args.Add("-hide_banner");
    args.Add("-loglevel");
    args.Add("error");

    // ffmpeg progress lives on stderr in the form "frame= NNN" — keep
    // that on by NOT silencing -stats. We force -progress to stderr
    // explicitly so the parser gets fresh values even when -loglevel
    // suppresses everything else.
    args.Add("-stats");
    args.Add("-y");

    args.Add(Path.Combine(outputDir.FullName, "frame_%05d.jpg"));
    return args;
  }

  public async Task<IReadOnlyList<FileInfo>> ExtractAsync(
    FileInfo videoFile,
    DirectoryInfo outputDir,
    VideoExtractOptions options,
    IProgress<int>? progress,
    CancellationToken cancellationToken
  ) {
    if (!videoFile.Exists)
      throw new FileNotFoundException("Video file not found", videoFile.FullName);

    var ffmpeg = this._ffmpegOverride ?? FfmpegLocator.Find();
    if (ffmpeg is null || !ffmpeg.Exists)
      throw new InvalidOperationException("ffmpeg not found. Install ffmpeg and ensure it's on PATH.");

    outputDir.Create();

    var args = BuildCommandLine(videoFile, outputDir, options);
    var psi = new ProcessStartInfo {
      FileName = ffmpeg.FullName,
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (var a in args)
      psi.ArgumentList.Add(a);

    using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
    var stderrBuffer = new System.Text.StringBuilder();
    proc.ErrorDataReceived += (_, e) => {
      if (e.Data is null)
        return;
      stderrBuffer.AppendLine(e.Data);
      if (progress is null)
        return;
      var match = FrameProgressRegex.Match(e.Data);
      if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
        progress.Report(frame);
    };

    if (!proc.Start())
      throw new InvalidOperationException("Failed to start ffmpeg process.");
    proc.BeginErrorReadLine();
    // Drain stdout so the buffer can't deadlock — ffmpeg shouldn't
    // write to it given our flags, but be defensive.
    _ = proc.StandardOutput.ReadToEndAsync();

    using (cancellationToken.Register(() => TryKill(proc))) {
      try {
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        TryKill(proc);
        throw;
      }
    }

    if (proc.ExitCode != 0) {
      var tail = stderrBuffer.ToString();
      if (tail.Length > 800)
        tail = tail[^800..];
      throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode}. {tail}");
    }

    return outputDir
      .EnumerateFiles("frame_*.jpg")
      .OrderBy(f => f.Name, StringComparer.Ordinal)
      .ToList();
  }

  private static void TryKill(Process proc) {
    try {
      if (!proc.HasExited)
        proc.Kill(entireProcessTree: true);
    } catch {
      // Already gone — fine.
    }
  }

  private static string FormatTimeSpan(TimeSpan ts) {
    var inv = CultureInfo.InvariantCulture;
    // hh:mm:ss.fff — ffmpeg accepts this universally and keeps full
    // millisecond precision. TimeSpan.ToString("c") drops trailing
    // zeros which ffmpeg misparses on some platforms.
    var totalHours = (int)Math.Floor(ts.TotalHours);
    return string.Create(inv, $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}");
  }
}
