using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Hawkynt.PhotoManager.Core.Metadata;

/// <summary>
/// A single pending metadata write: the file to patch, the edit to apply, and
/// bookkeeping for retry logic. Serialized to disk so writes survive crashes.
/// </summary>
public sealed record WriteBackItem {
  public string FilePath { get; init; } = string.Empty;
  public MetadataEditDto Edit { get; init; } = new();
  public DateTime EnqueuedUtc { get; init; }
  public int RetryCount { get; init; }

  /// <summary>
  /// Optional human-readable description shown in the UI queue list.
  /// Not used by the write logic itself.
  /// </summary>
  public string? Description { get; init; }
}

/// <summary>
/// Crash-safe retry queue for metadata writes. Pending items are persisted to
/// JSON on disk so they survive process termination. A single background
/// consumer drains the channel, retrying with exponential backoff on transient
/// I/O failures and moving permanently-failed items to a separate list.
/// </summary>
public sealed class WriteBackQueue : IDisposable {
  public const int MaxRetries = 10;

  private static readonly TimeSpan[] BackoffSchedule = {
    TimeSpan.FromSeconds(1),
    TimeSpan.FromSeconds(5),
    TimeSpan.FromSeconds(30),
    TimeSpan.FromMinutes(5),
    TimeSpan.FromMinutes(30)
  };

  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private readonly DirectoryInfo _storeDir;
  private readonly IMetadataWriter _writer;
  private readonly Func<int, TimeSpan> _backoffProvider;
  private readonly Channel<WriteBackItem> _channel;
  private readonly CancellationTokenSource _cts = new();
  private readonly object _lock = new();

  private List<WriteBackItem> _pending = new();
  private List<WriteBackItem> _failed = new();
  private Task? _consumer;

  /// <summary>Raised on the thread-pool whenever the pending or failed lists change.</summary>
  public event Action? QueueChanged;

  /// <summary>
  /// Creates a new queue backed by the given directory. If the directory
  /// contains a <c>pending.json</c> from a previous session, those items
  /// are loaded and re-queued automatically.
  /// </summary>
  /// <param name="writer">The underlying metadata writer.</param>
  /// <param name="storeDir">Persistence directory (defaults to AppDataPaths).</param>
  /// <param name="backoffProvider">
  /// Optional function mapping retry count (1-based) to delay duration.
  /// Pass <c>_ => TimeSpan.Zero</c> in tests to eliminate wait times.
  /// </param>
  public WriteBackQueue(IMetadataWriter writer, DirectoryInfo? storeDir = null,
                        Func<int, TimeSpan>? backoffProvider = null) {
    ArgumentNullException.ThrowIfNull(writer);
    this._writer = writer;
    this._storeDir = storeDir ?? AppDataPaths.SubDirectory("write-queue");
    this._backoffProvider = backoffProvider ?? DefaultBackoff;
    this._channel = Channel.CreateUnbounded<WriteBackItem>(
      new UnboundedChannelOptions { SingleReader = true });

    this.LoadFromDisk();
  }

  private static TimeSpan DefaultBackoff(int retryCount) {
    var index = Math.Min(retryCount - 1, BackoffSchedule.Length - 1);
    return BackoffSchedule[index];
  }

  /// <summary>Starts the background consumer. Call once after construction.</summary>
  public void Start() {
    if (this._consumer is not null)
      return;

    // Re-enqueue any items loaded from disk.
    lock (this._lock) {
      foreach (var item in this._pending)
        this._channel.Writer.TryWrite(item);
    }

    this._consumer = Task.Run(() => this.ConsumeAsync(this._cts.Token));
  }

  /// <summary>Adds a metadata write to the queue. Persists immediately.</summary>
  public void Enqueue(WriteBackItem item) {
    ArgumentNullException.ThrowIfNull(item);

    lock (this._lock) {
      this._pending.Add(item);
      this.PersistPending();
    }

    this._channel.Writer.TryWrite(item);
    this.QueueChanged?.Invoke();
  }

  /// <summary>
  /// Convenience overload: wraps a file + edit into a <see cref="WriteBackItem"/>
  /// and enqueues it.
  /// </summary>
  public void Enqueue(FileInfo imageFile, MetadataEdit edit, string? description = null) {
    ArgumentNullException.ThrowIfNull(imageFile);
    ArgumentNullException.ThrowIfNull(edit);

    Enqueue(new WriteBackItem {
      FilePath = imageFile.FullName,
      Edit = MetadataEditDto.FromEdit(edit),
      EnqueuedUtc = DateTime.UtcNow,
      RetryCount = 0,
      Description = description
    });
  }

  public IReadOnlyList<WriteBackItem> GetPending() {
    lock (this._lock)
      return this._pending.ToList();
  }

  public IReadOnlyList<WriteBackItem> GetFailed() {
    lock (this._lock)
      return this._failed.ToList();
  }

  /// <summary>Moves a failed item back into the pending queue for another round of retries.</summary>
  public void RetryFailed(WriteBackItem item) {
    ArgumentNullException.ThrowIfNull(item);

    lock (this._lock) {
      this._failed.Remove(item);
      this.PersistFailed();

      var retryItem = item with { RetryCount = 0 };
      this._pending.Add(retryItem);
      this.PersistPending();
      this._channel.Writer.TryWrite(retryItem);
    }

    this.QueueChanged?.Invoke();
  }

  /// <summary>Permanently removes a failed item. The write is abandoned.</summary>
  public void DiscardFailed(WriteBackItem item) {
    ArgumentNullException.ThrowIfNull(item);

    lock (this._lock) {
      this._failed.Remove(item);
      this.PersistFailed();
    }

    this.QueueChanged?.Invoke();
  }

  public void Dispose() {
    this._cts.Cancel();
    this._channel.Writer.TryComplete();
    try { this._consumer?.Wait(); } catch { /* shutdown */ }
    this._cts.Dispose();
  }

  // ── Background consumer ───────────────────────────────────────────────

  private async Task ConsumeAsync(CancellationToken ct) {
    await foreach (var item in this._channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
      if (ct.IsCancellationRequested)
        break;

      // Backoff delay before retries (first attempt has RetryCount 0 → no delay).
      if (item.RetryCount > 0) {
        var delay = this._backoffProvider(item.RetryCount);
        if (delay > TimeSpan.Zero) {
          try {
            await Task.Delay(delay, ct).ConfigureAwait(false);
          } catch (OperationCanceledException) {
            break;
          }
        }
      }

      try {
        var edit = item.Edit.ToMetadataEdit();
        await this._writer.ApplyAsync(new FileInfo(item.FilePath), edit, ct).ConfigureAwait(false);

        // Success — remove from pending.
        lock (this._lock) {
          RemoveByRef(this._pending, item);
          this.PersistPending();
        }

        this.QueueChanged?.Invoke();
      } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        var nextRetry = item.RetryCount + 1;

        lock (this._lock) {
          RemoveByRef(this._pending, item);

          if (nextRetry >= MaxRetries) {
            // Exhausted retries — move to failed.
            this._failed.Add(item with { RetryCount = nextRetry });
            this.PersistFailed();
          } else {
            // Re-enqueue with incremented count.
            var retryItem = item with { RetryCount = nextRetry };
            this._pending.Add(retryItem);
            this._channel.Writer.TryWrite(retryItem);
          }

          this.PersistPending();
        }

        this.QueueChanged?.Invoke();
      } catch (OperationCanceledException) {
        break;
      } catch {
        // Unexpected error (corrupt edit, etc.) — treat as permanent failure.
        lock (this._lock) {
          RemoveByRef(this._pending, item);
          this._failed.Add(item with { RetryCount = item.RetryCount + 1 });
          this.PersistPending();
          this.PersistFailed();
        }

        this.QueueChanged?.Invoke();
      }
    }
  }

  // ── Persistence ───────────────────────────────────────────────────────

  private string PendingPath => Path.Combine(this._storeDir.FullName, "pending.json");
  private string FailedPath => Path.Combine(this._storeDir.FullName, "failed.json");

  private void LoadFromDisk() {
    this._pending = LoadList(this.PendingPath);
    this._failed = LoadList(this.FailedPath);
  }

  private static List<WriteBackItem> LoadList(string path) {
    try {
      if (!File.Exists(path))
        return new List<WriteBackItem>();

      var json = File.ReadAllText(path);
      if (string.IsNullOrWhiteSpace(json))
        return new List<WriteBackItem>();

      return JsonSerializer.Deserialize<List<WriteBackItem>>(json, JsonOptions)
             ?? new List<WriteBackItem>();
    } catch {
      return new List<WriteBackItem>();
    }
  }

  /// <summary>Must be called under <see cref="_lock"/>.</summary>
  private void PersistPending() => PersistList(this.PendingPath, this._pending);

  /// <summary>Must be called under <see cref="_lock"/>.</summary>
  private void PersistFailed() => PersistList(this.FailedPath, this._failed);

  private void PersistList(string path, List<WriteBackItem> items) {
    try {
      if (!this._storeDir.Exists)
        this._storeDir.Create();

      var json = JsonSerializer.Serialize(items, JsonOptions);
      var tmp = path + ".tmp";
      File.WriteAllText(tmp, json);

      if (File.Exists(path))
        File.Replace(tmp, path, destinationBackupFileName: null);
      else
        File.Move(tmp, path);
    } catch {
      // Best-effort persistence — if disk fails we still have the in-memory list.
    }
  }

  /// <summary>
  /// Removes the first occurrence of <paramref name="target"/> from
  /// <paramref name="list"/> using reference equality. We need this
  /// because the consumer holds the exact object reference it received
  /// from the channel, and record value-equality can be unreliable
  /// when nested types (e.g. <c>List&lt;string&gt;</c>) don't implement
  /// structural equality.
  /// </summary>
  private static void RemoveByRef(List<WriteBackItem> list, WriteBackItem target) {
    for (var i = 0; i < list.Count; i++) {
      if (ReferenceEquals(list[i], target)) {
        list.RemoveAt(i);
        return;
      }
    }

    // Fallback: value-equality scan (e.g. items loaded from disk).
    list.Remove(target);
  }
}

/// <summary>
/// Serializable DTO that captures the subset of <see cref="MetadataEdit"/>
/// fields that carry values. The queue persists this instead of the full
/// <see cref="MetadataEdit"/> because <see cref="Optional{T}"/> uses a
/// private constructor that <see cref="JsonSerializer"/> can't roundtrip
/// without a custom converter. This DTO only stores fields that were
/// actually set, sidestepping the three-state problem entirely.
/// </summary>
public sealed record MetadataEditDto {
  public double? GpsLatitude { get; init; }
  public double? GpsLongitude { get; init; }
  public double? GpsAltitude { get; init; }
  public bool HasGps { get; init; }

  public int? Rating { get; init; }
  public bool HasRating { get; init; }

  public string? ColorLabel { get; init; }
  public bool HasColorLabel { get; init; }

  public string? Title { get; init; }
  public bool HasTitle { get; init; }

  public string? Caption { get; init; }
  public bool HasCaption { get; init; }

  public string? Creator { get; init; }
  public bool HasCreator { get; init; }

  public string? Copyright { get; init; }
  public bool HasCopyright { get; init; }

  public string? Location { get; init; }
  public bool HasLocation { get; init; }

  public string? City { get; init; }
  public bool HasCity { get; init; }

  public string? State { get; init; }
  public bool HasState { get; init; }

  public string? Country { get; init; }
  public bool HasCountry { get; init; }

  public string? CountryCode { get; init; }
  public bool HasCountryCode { get; init; }

  public List<string>? Keywords { get; init; }
  public bool HasKeywords { get; init; }

  public DateTime? DateCreated { get; init; }
  public bool HasDateCreated { get; init; }

  public bool? IsPick { get; init; }
  public bool HasIsPick { get; init; }

  public bool? IsReject { get; init; }
  public bool HasIsReject { get; init; }

  /// <summary>Converts this DTO back to a live <see cref="MetadataEdit"/>.</summary>
  public MetadataEdit ToMetadataEdit() {
    var edit = new MetadataEdit();

    if (this.HasGps)
      edit = edit with {
        Gps = this.GpsLatitude is { } lat && this.GpsLongitude is { } lon
          ? new GpsCoordinate(lat, lon, this.GpsAltitude)
          : Optional<GpsCoordinate?>.Set(null)
      };

    if (this.HasRating)
      edit = edit with { Rating = Optional<int?>.Set(this.Rating) };

    if (this.HasColorLabel)
      edit = edit with { ColorLabel = Optional<string?>.Set(this.ColorLabel) };

    if (this.HasTitle)
      edit = edit with { Title = Optional<string?>.Set(this.Title) };

    if (this.HasCaption)
      edit = edit with { Caption = Optional<string?>.Set(this.Caption) };

    if (this.HasCreator)
      edit = edit with { Creator = Optional<string?>.Set(this.Creator) };

    if (this.HasCopyright)
      edit = edit with { Copyright = Optional<string?>.Set(this.Copyright) };

    if (this.HasLocation)
      edit = edit with { Location = Optional<string?>.Set(this.Location) };

    if (this.HasCity)
      edit = edit with { City = Optional<string?>.Set(this.City) };

    if (this.HasState)
      edit = edit with { State = Optional<string?>.Set(this.State) };

    if (this.HasCountry)
      edit = edit with { Country = Optional<string?>.Set(this.Country) };

    if (this.HasCountryCode)
      edit = edit with { CountryCode = Optional<string?>.Set(this.CountryCode) };

    if (this.HasKeywords)
      edit = edit with { Keywords = Optional<IReadOnlyList<string>>.Set(
        this.Keywords?.ToArray() ?? Array.Empty<string>()) };

    if (this.HasDateCreated)
      edit = edit with { DateCreated = Optional<DateTime?>.Set(this.DateCreated) };

    if (this.HasIsPick)
      edit = edit with { IsPick = Optional<bool?>.Set(this.IsPick) };

    if (this.HasIsReject)
      edit = edit with { IsReject = Optional<bool?>.Set(this.IsReject) };

    return edit;
  }

  /// <summary>Snapshots a <see cref="MetadataEdit"/> into this DTO, capturing only set fields.</summary>
  public static MetadataEditDto FromEdit(MetadataEdit edit) {
    var dto = new MetadataEditDto();

    if (edit.Gps.HasValue) {
      dto = dto with {
        HasGps = true,
        GpsLatitude = edit.Gps.Value?.Latitude,
        GpsLongitude = edit.Gps.Value?.Longitude,
        GpsAltitude = edit.Gps.Value?.AltitudeMeters
      };
    }

    if (edit.Rating.HasValue)
      dto = dto with { HasRating = true, Rating = edit.Rating.Value };

    if (edit.ColorLabel.HasValue)
      dto = dto with { HasColorLabel = true, ColorLabel = edit.ColorLabel.Value };

    if (edit.Title.HasValue)
      dto = dto with { HasTitle = true, Title = edit.Title.Value };

    if (edit.Caption.HasValue)
      dto = dto with { HasCaption = true, Caption = edit.Caption.Value };

    if (edit.Creator.HasValue)
      dto = dto with { HasCreator = true, Creator = edit.Creator.Value };

    if (edit.Copyright.HasValue)
      dto = dto with { HasCopyright = true, Copyright = edit.Copyright.Value };

    if (edit.Location.HasValue)
      dto = dto with { HasLocation = true, Location = edit.Location.Value };

    if (edit.City.HasValue)
      dto = dto with { HasCity = true, City = edit.City.Value };

    if (edit.State.HasValue)
      dto = dto with { HasState = true, State = edit.State.Value };

    if (edit.Country.HasValue)
      dto = dto with { HasCountry = true, Country = edit.Country.Value };

    if (edit.CountryCode.HasValue)
      dto = dto with { HasCountryCode = true, CountryCode = edit.CountryCode.Value };

    if (edit.Keywords.HasValue)
      dto = dto with { HasKeywords = true, Keywords = edit.Keywords.Value?.ToList() };

    if (edit.DateCreated.HasValue)
      dto = dto with { HasDateCreated = true, DateCreated = edit.DateCreated.Value };

    if (edit.IsPick.HasValue)
      dto = dto with { HasIsPick = true, IsPick = edit.IsPick.Value };

    if (edit.IsReject.HasValue)
      dto = dto with { HasIsReject = true, IsReject = edit.IsReject.Value };

    return dto;
  }
}
