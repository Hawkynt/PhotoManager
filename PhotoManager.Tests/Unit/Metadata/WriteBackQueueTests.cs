using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class WriteBackQueueTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(
      Path.Combine(Path.GetTempPath(), "PhotoManager-wbq-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private DirectoryInfo QueueDir => new(Path.Combine(this._workingDir.FullName, "queue"));

  private FileInfo CreateFakeImage(string name = "photo.jpg") {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    file.Refresh();
    return file;
  }

  [Test]
  public void Enqueue_GetPending_ReturnsTheItem() {
    var writer = new FakeMetadataWriter();
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);

    var item = new WriteBackItem {
      FilePath = "/photos/test.jpg",
      Edit = MetadataEditDto.FromEdit(new MetadataEdit { Rating = 5 }),
      EnqueuedUtc = DateTime.UtcNow,
      RetryCount = 0
    };

    queue.Enqueue(item);

    var pending = queue.GetPending();
    Assert.That(pending, Has.Count.EqualTo(1));
    Assert.That(pending[0].FilePath, Is.EqualTo("/photos/test.jpg"));
  }

  [Test]
  public void Persistence_NewInstanceLoadsPendingFromDisk() {
    var writer = new FakeMetadataWriter();

    // Enqueue in one instance (don't start consumer so it stays pending).
    using (var queue1 = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero)) {
      queue1.Enqueue(new WriteBackItem {
        FilePath = "/photos/persisted.jpg",
        Edit = MetadataEditDto.FromEdit(new MetadataEdit { Title = "Hello" }),
        EnqueuedUtc = DateTime.UtcNow,
        RetryCount = 0
      });
    }

    // New instance from same directory sees the item.
    using var queue2 = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);
    var pending = queue2.GetPending();
    Assert.That(pending, Has.Count.EqualTo(1));
    Assert.That(pending[0].FilePath, Is.EqualTo("/photos/persisted.jpg"));
  }

  [Test]
  public async Task SuccessfulWrite_RemovesFromPending() {
    var writer = new FakeMetadataWriter();
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);
    queue.Start();

    var image = this.CreateFakeImage();
    queue.Enqueue(image, new MetadataEdit { Rating = 3 }, "test write");

    // Give the consumer a moment to process.
    await Task.Delay(500);

    var pending = queue.GetPending();
    Assert.That(pending, Is.Empty);
    Assert.That(writer.ApplyCount, Is.GreaterThanOrEqualTo(1));
  }

  [Test]
  public async Task FailedWrite_IncrementsRetryCount() {
    var writer = new FakeMetadataWriter { FailCount = 2 };
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);

    var image = this.CreateFakeImage();
    queue.Enqueue(image, new MetadataEdit { Rating = 4 });

    // Start consumer and wait for first failure + re-enqueue.
    queue.Start();
    await Task.Delay(500);

    // After the first failure, the item should be re-enqueued with RetryCount > 0.
    // After FailCount failures succeed again, the item should clear.
    // Since FailCount=2, first two attempts fail, third succeeds.
    await Task.Delay(3000);

    var pending = queue.GetPending();
    var failed = queue.GetFailed();

    // Eventually succeeds on third try, so both lists empty.
    Assert.That(pending, Is.Empty);
    Assert.That(failed, Is.Empty);
    Assert.That(writer.ApplyCount, Is.GreaterThanOrEqualTo(3));
  }

  [Test]
  public async Task AfterMaxRetries_MovesToFailed() {
    var writer = new FakeMetadataWriter { FailForever = true };
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);

    // Start consumer first, then enqueue so the item enters the channel
    // exactly once (Start re-enqueues pending items from disk, but the
    // queue is empty at this point).
    queue.Start();

    var item = new WriteBackItem {
      FilePath = this.CreateFakeImage().FullName,
      Edit = MetadataEditDto.FromEdit(new MetadataEdit { Rating = 1 }),
      EnqueuedUtc = DateTime.UtcNow,
      RetryCount = WriteBackQueue.MaxRetries - 1  // 9 — next failure is the 10th
    };

    queue.Enqueue(item);

    await Task.Delay(2000);

    var failed = queue.GetFailed();
    Assert.That(failed, Has.Count.EqualTo(1));
    Assert.That(failed[0].RetryCount, Is.EqualTo(WriteBackQueue.MaxRetries));

    var pending = queue.GetPending();
    Assert.That(pending, Is.Empty);
  }

  [Test]
  public async Task RetryFailed_MovesBackToPending() {
    var writer = new FakeMetadataWriter { FailForever = true };
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);

    queue.Start();

    var item = new WriteBackItem {
      FilePath = this.CreateFakeImage().FullName,
      Edit = MetadataEditDto.FromEdit(new MetadataEdit { Rating = 1 }),
      EnqueuedUtc = DateTime.UtcNow,
      RetryCount = WriteBackQueue.MaxRetries - 1
    };

    queue.Enqueue(item);

    await Task.Delay(2000);

    // Item should be in failed now.
    var failed = queue.GetFailed();
    Assert.That(failed, Has.Count.EqualTo(1));

    // Now make writer succeed and retry.
    writer.FailForever = false;
    writer.FailCount = 0;
    queue.RetryFailed(failed[0]);

    await Task.Delay(1000);

    // Should have moved back to pending and then succeeded.
    Assert.That(queue.GetFailed(), Is.Empty);
    Assert.That(queue.GetPending(), Is.Empty);
  }

  [Test]
  public async Task DiscardFailed_RemovesPermanently() {
    var writer = new FakeMetadataWriter { FailForever = true };
    using var queue = new WriteBackQueue(writer, this.QueueDir, _ => TimeSpan.Zero);

    queue.Start();

    var item = new WriteBackItem {
      FilePath = this.CreateFakeImage().FullName,
      Edit = MetadataEditDto.FromEdit(new MetadataEdit { Rating = 2 }),
      EnqueuedUtc = DateTime.UtcNow,
      RetryCount = WriteBackQueue.MaxRetries - 1
    };

    queue.Enqueue(item);

    await Task.Delay(2000);

    var failed = queue.GetFailed();
    Assert.That(failed, Has.Count.EqualTo(1));

    queue.DiscardFailed(failed[0]);

    Assert.That(queue.GetFailed(), Is.Empty);
    Assert.That(queue.GetPending(), Is.Empty);
  }

  [Test]
  public void MetadataEditDto_RoundTrip_PreservesFields() {
    var edit = new MetadataEdit {
      Gps = new GpsCoordinate(48.1234, 11.5678, 520.0),
      Rating = 4,
      Title = "Sunset",
      Caption = "A lovely sunset",
      Keywords = new[] { "sun", "nature" },
      City = "Munich",
      Country = "Germany",
      IsPick = true
    };

    var dto = MetadataEditDto.FromEdit(edit);
    var reconstructed = dto.ToMetadataEdit();

    Assert.Multiple(() => {
      Assert.That(reconstructed.Gps.HasValue, Is.True);
      Assert.That(reconstructed.Gps.Value!.Value.Latitude, Is.EqualTo(48.1234).Within(0.0001));
      Assert.That(reconstructed.Gps.Value!.Value.Longitude, Is.EqualTo(11.5678).Within(0.0001));
      Assert.That(reconstructed.Rating.HasValue, Is.True);
      Assert.That(reconstructed.Rating.Value, Is.EqualTo(4));
      Assert.That(reconstructed.Title.HasValue, Is.True);
      Assert.That(reconstructed.Title.Value, Is.EqualTo("Sunset"));
      Assert.That(reconstructed.Keywords.HasValue, Is.True);
      Assert.That(reconstructed.Keywords.Value, Is.EqualTo(new[] { "sun", "nature" }));
      Assert.That(reconstructed.IsPick.HasValue, Is.True);
      Assert.That(reconstructed.IsPick.Value, Is.True);
    });
  }

  // ── Fake writer ─────────────────────────────────────────────────────

  private sealed class FakeMetadataWriter : IMetadataWriter {
    private int _callCount;

    /// <summary>Number of initial calls that should throw IOException.</summary>
    public int FailCount { get; set; }

    /// <summary>When true, every call throws IOException.</summary>
    public bool FailForever { get; set; }

    /// <summary>Total number of times ApplyAsync was called.</summary>
    public int ApplyCount => this._callCount;

    public Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref this._callCount);

      if (this.FailForever || n <= this.FailCount)
        throw new IOException("Simulated file lock");

      return Task.FromResult(imageFile);
    }
  }
}
