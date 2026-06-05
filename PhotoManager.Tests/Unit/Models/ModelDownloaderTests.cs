using System.Net;
using System.Net.Http;
using System.Text;
using Hawkynt.PhotoManager.Core.Models;

namespace Hawkynt.PhotoManager.Tests.Unit.Models;

[TestFixture]
public class ModelDownloaderTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-models-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private ModelDownloader BuildDownloader(Func<HttpRequestMessage, HttpResponseMessage> handler)
    => new(new HttpClient(new MockHandler(handler)));

  private FileInfo Destination(string name)
    => new(Path.Combine(this._workingDir.FullName, name));

  [Test]
  public async Task DownloadAsync_Success_WritesFileAndReportsProgress() {
    var payload = Encoding.ASCII.GetBytes(new string('x', 2048));
    using var downloader = this.BuildDownloader(_ => new HttpResponseMessage(HttpStatusCode.OK) {
      Content = new ByteArrayContent(payload) { Headers = { ContentLength = payload.Length } }
    });

    var destination = this.Destination("model.onnx");
    var reports = new List<ModelDownloadProgress>();
    var progress = new Progress<ModelDownloadProgress>(reports.Add);

    var result = await downloader.DownloadAsync("https://example.invalid/model", destination, progress);

    Assert.Multiple(() => {
      Assert.That(result.Exists, Is.True);
      Assert.That(result.Length, Is.EqualTo(payload.Length));
      Assert.That(reports.Count, Is.GreaterThan(0));
      Assert.That(reports[^1].BytesReceived, Is.EqualTo(payload.Length));
      Assert.That(reports[^1].TotalBytes, Is.EqualTo(payload.Length));
      Assert.That(File.Exists(destination.FullName + ".part"), Is.False, "temp file should not linger");
    });
  }

  [Test]
  public void DownloadAsync_HttpError_Throws_AndCleansUpTemp() {
    using var downloader = this.BuildDownloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    var destination = this.Destination("model.onnx");

    Assert.ThrowsAsync<HttpRequestException>(async () =>
      await downloader.DownloadAsync("https://example.invalid/model", destination));

    Assert.Multiple(() => {
      Assert.That(File.Exists(destination.FullName), Is.False);
      Assert.That(File.Exists(destination.FullName + ".part"), Is.False);
    });
  }

  [Test]
  public async Task DownloadAsync_OverwritesExisting_AtomicallyReplaces() {
    var destination = this.Destination("model.onnx");
    await File.WriteAllTextAsync(destination.FullName, "stale content");

    var payload = Encoding.ASCII.GetBytes("fresh content");
    using var downloader = this.BuildDownloader(_ => new HttpResponseMessage(HttpStatusCode.OK) {
      Content = new ByteArrayContent(payload) { Headers = { ContentLength = payload.Length } }
    });

    await downloader.DownloadAsync("https://example.invalid/model", destination);

    var contents = await File.ReadAllBytesAsync(destination.FullName);
    Assert.That(contents, Is.EqualTo(payload).AsCollection);
  }

  [Test]
  public void DownloadAsync_NullDestination_Throws() {
    using var downloader = this.BuildDownloader(_ => new HttpResponseMessage(HttpStatusCode.OK));
    Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await downloader.DownloadAsync("https://example.invalid", destination: null!));
  }

  [Test]
  public void DownloadAsync_BlankUrl_Throws() {
    using var downloader = this.BuildDownloader(_ => new HttpResponseMessage(HttpStatusCode.OK));
    Assert.ThrowsAsync<ArgumentException>(async () =>
      await downloader.DownloadAsync("  ", this.Destination("m.onnx")));
  }

  [Test]
  public void Progress_Fraction_NullWhenTotalUnknown() {
    var p = new ModelDownloadProgress(1024, TotalBytes: null);
    Assert.That(p.Fraction, Is.Null);
  }

  [Test]
  public void Progress_Fraction_ComputesCorrectly() {
    Assert.Multiple(() => {
      Assert.That(new ModelDownloadProgress(0, 1024).Fraction, Is.EqualTo(0).Within(1e-9));
      Assert.That(new ModelDownloadProgress(512, 1024).Fraction, Is.EqualTo(0.5).Within(1e-9));
      Assert.That(new ModelDownloadProgress(1024, 1024).Fraction, Is.EqualTo(1).Within(1e-9));
    });
  }

  [Test]
  public void Registry_FindByName_CaseInsensitive() {
    Assert.Multiple(() => {
      Assert.That(ModelRegistry.FindByName("yolov8n"), Is.SameAs(ModelRegistry.YoloV8n));
      Assert.That(ModelRegistry.FindByName("YOLOV8N"), Is.SameAs(ModelRegistry.YoloV8n));
      Assert.That(ModelRegistry.FindByName("ultraface-rfb"), Is.SameAs(ModelRegistry.UltraFaceRfb320));
      Assert.That(ModelRegistry.FindByName("not-a-real-model"), Is.Null);
    });
  }

  [Test]
  public void Registry_AllModelsHaveRequiredFields() {
    foreach (var model in ModelRegistry.All) {
      Assert.That(model.Name, Is.Not.Empty);
      Assert.That(model.FileName, Does.EndWith(".onnx"));
      Assert.That(model.ApproximateSizeBytes, Is.GreaterThan(0));
      // Empty DownloadUrl is allowed for manual-install-only models
      // (e.g. DDColor — only on Google Drive, no public HF mirror as
      // of writing). Non-empty URLs must be HTTPS so the downloader
      // can't be MITM'd into pulling tampered weights.
      if (!string.IsNullOrEmpty(model.DownloadUrl))
        Assert.That(model.DownloadUrl, Does.StartWith("https://"),
          $"model {model.Name} should use HTTPS to avoid MITM tampering of weights");
    }
  }

  private sealed class MockHandler : HttpMessageHandler {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => this._handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => Task.FromResult(this._handler(request));
  }
}
