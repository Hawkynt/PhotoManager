using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hawkynt.PhotoManager.Core.Previews;

/// <summary>
/// Background worker that pre-generates thumbnails for a batch of files so
/// the grid can scroll without blocking on first-time thumbnail extraction.
/// Thumbnails are pushed into an <see cref="InMemoryPreviewCache"/> that the
/// on-demand loader also reads from, so pre-cached entries are served
/// instantly when the user scrolls to them.
///
/// Uses <see cref="Channel{T}"/> as a bounded work queue and a single
/// long-running consumer task. Memory is bounded by the cache's own limits;
/// duplicate enqueues for the same file path are suppressed.
/// </summary>
public sealed class ThumbnailPreCacheService : IDisposable {
  private const int DefaultMaxEdge = 1600;
  private const int DefaultChannelCapacity = 10_000;

  private readonly InMemoryPreviewCache _cache;
  private readonly int _maxEdge;
  private readonly Channel<FileInfo> _channel;
  private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
  private readonly object _gate = new();

  private CancellationTokenSource? _cts;
  private Task? _consumer;
  private int _processedCount;
  private int _totalQueued;

  /// <summary>
  /// Delegate that decodes + resizes a file into JPEG bytes suitable for
  /// caching. Injected so the service can be tested without touching disk.
  /// The production callsite wires this to the same decode path as
  /// <c>ImagePreviewLoader</c>.
  /// </summary>
  public delegate Task<byte[]?> ThumbnailGenerator(FileInfo file, CancellationToken cancellationToken);

  private readonly ThumbnailGenerator _generator;

  public ThumbnailPreCacheService(
    InMemoryPreviewCache cache,
    ThumbnailGenerator generator,
    int maxEdge = DefaultMaxEdge,
    int channelCapacity = DefaultChannelCapacity
  ) {
    this._cache = cache ?? throw new ArgumentNullException(nameof(cache));
    this._generator = generator ?? throw new ArgumentNullException(nameof(generator));
    this._maxEdge = maxEdge;
    this._channel = Channel.CreateBounded<FileInfo>(new BoundedChannelOptions(channelCapacity) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false
    });
  }

  /// <summary>Number of files that have been processed (successfully or not).</summary>
  public int ProcessedCount => Volatile.Read(ref this._processedCount);

  /// <summary>Total number of files that were queued via <see cref="Enqueue"/>.</summary>
  public int TotalQueued => Volatile.Read(ref this._totalQueued);

  /// <summary>
  /// Whether the background consumer is still running. False once cancelled
  /// or the channel is drained after completion.
  /// </summary>
  public bool IsRunning {
    get {
      lock (this._gate)
        return this._consumer is { IsCompleted: false };
    }
  }

  /// <summary>
  /// Adds files to the background processing queue. Starts the consumer
  /// task if it isn't already running. Duplicate paths (case-insensitive)
  /// within the same session are silently skipped.
  /// </summary>
  public void Enqueue(IEnumerable<FileInfo> files) {
    ArgumentNullException.ThrowIfNull(files);

    var added = 0;
    foreach (var file in files) {
      if (!this._seen.TryAdd(file.FullName, 0))
        continue;
      // TryWrite on a bounded channel with DropOldest never returns false,
      // but guard anyway.
      this._channel.Writer.TryWrite(file);
      added++;
    }

    Interlocked.Add(ref this._totalQueued, added);
    this.EnsureConsumerRunning();
  }

  /// <summary>
  /// Returns the cached JPEG bytes for <paramref name="file"/> if they
  /// have been pre-generated, or null if the file hasn't been processed yet.
  /// </summary>
  public byte[]? TryGetCached(FileInfo file) {
    var key = PreviewCacheKey.For(file, this._maxEdge);
    return this._cache.TryGet(key, out var bytes) ? bytes : null;
  }

  /// <summary>
  /// Cancel any in-progress background work and reset state so a new batch
  /// can be enqueued (e.g., when the user changes the source folder).
  /// </summary>
  public void Cancel() {
    CancellationTokenSource? cts;
    Task? consumer;
    lock (this._gate) {
      cts = this._cts;
      consumer = this._consumer;
      this._cts = null;
      this._consumer = null;
    }

    cts?.Cancel();
    try { consumer?.Wait(TimeSpan.FromSeconds(2)); } catch { /* swallow */ }
    cts?.Dispose();

    this._seen.Clear();
    Interlocked.Exchange(ref this._processedCount, 0);
    Interlocked.Exchange(ref this._totalQueued, 0);

    // Drain any leftover items so a subsequent Enqueue starts clean.
    while (this._channel.Reader.TryRead(out _)) { }
  }

  public void Dispose() {
    this.Cancel();
  }

  private void EnsureConsumerRunning() {
    lock (this._gate) {
      if (this._consumer is { IsCompleted: false })
        return;

      this._cts?.Dispose();
      this._cts = new CancellationTokenSource();
      var token = this._cts.Token;
      this._consumer = Task.Factory.StartNew(
        () => this.ConsumeAsync(token),
        token,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default
      ).Unwrap();
    }
  }

  private async Task ConsumeAsync(CancellationToken cancellationToken) {
    try {
      await foreach (var file in this._channel.Reader.ReadAllAsync(cancellationToken)) {
        if (cancellationToken.IsCancellationRequested)
          break;

        try {
          var key = PreviewCacheKey.For(file, this._maxEdge);
          if (this._cache.TryGet(key, out _)) {
            // Already cached (e.g. by the on-demand loader while we were
            // queued). Count as processed.
            Interlocked.Increment(ref this._processedCount);
            continue;
          }

          var bytes = await this._generator(file, cancellationToken);
          if (bytes is { Length: > 0 })
            this._cache.Set(key, bytes);
        } catch (OperationCanceledException) {
          throw;
        } catch {
          // Individual file failures must not kill the consumer.
        }

        Interlocked.Increment(ref this._processedCount);
      }
    } catch (OperationCanceledException) {
      // Expected on cancellation.
    }
  }
}
