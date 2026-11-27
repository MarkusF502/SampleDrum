using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

internal enum WebOpKind { PieceSample, Preset, Test }

/// <summary>A command accepted from a phone: its clientOpId, the device and the controller operation (null for a test).</summary>
internal sealed class WebOp
{
    public required Guid OpId { get; init; }
    public required string ClientOpId { get; init; }
    public required string DeviceKey { get; init; }
    public required WebOpKind Kind { get; init; }
    public required string Fingerprint { get; init; }
    public DrumPiece? Piece { get; init; }
    public string? SampleId { get; init; }
    public string? PresetId { get; init; }
    public KitOperation? Operation { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Hub bookkeeping: last status/confirmation pushed as an `op` event.</summary>
    public string? LastSignature { get; set; }

    public bool IsPending => Operation?.Snapshot.Status is KitOperationStatus.Queued or KitOperationStatus.Preparing;

    /// <summary>True when the operation targets <paramref name="piece"/> (a preset targets every piece).</summary>
    public bool Targets(DrumPiece piece) => Kind == WebOpKind.Preset || Piece == piece;
}

/// <summary>
/// Idempotency (deviceKey + clientOpId → same operation) and lookup by opId. Keeps the last
/// <see cref="PerDeviceHistory"/> operations per device. Thread-safe.
/// </summary>
internal sealed class WebOperations
{
    public const int PerDeviceHistory = 512;

    private readonly object _gate = new();
    private readonly Dictionary<(string Device, string ClientOpId), WebOp> _byClient = [];
    private readonly Dictionary<Guid, WebOp> _byOpId = [];
    private readonly Dictionary<string, Queue<WebOp>> _perDevice = new(StringComparer.Ordinal);

    public WebOp? Find(string deviceKey, string clientOpId)
    { lock (_gate) return _byClient.TryGetValue((deviceKey, clientOpId), out var w) ? w : null; }

    public WebOp? Find(Guid opId)
    { lock (_gate) return _byOpId.TryGetValue(opId, out var w) ? w : null; }

    /// <summary>Adds the operation unless one already exists for the same device + clientOpId (returned instead).</summary>
    public WebOp AddOrGet(WebOp op)
    {
        lock (_gate)
        {
            if (_byClient.TryGetValue((op.DeviceKey, op.ClientOpId), out var existing)) return existing;
            _byClient[(op.DeviceKey, op.ClientOpId)] = op;
            _byOpId[op.OpId] = op;
            if (!_perDevice.TryGetValue(op.DeviceKey, out var q)) _perDevice[op.DeviceKey] = q = new Queue<WebOp>();
            q.Enqueue(op);
            while (q.Count > PerDeviceHistory)
            {
                var old = q.Dequeue();
                _byClient.Remove((old.DeviceKey, old.ClientOpId));
                _byOpId.Remove(old.OpId);
            }
            return op;
        }
    }

    /// <summary>Forgets a revoked device's history.</summary>
    public void RemoveDevice(string deviceKey)
    {
        lock (_gate)
        {
            if (!_perDevice.Remove(deviceKey, out var q)) return;
            foreach (var old in q)
            {
                _byClient.Remove((old.DeviceKey, old.ClientOpId));
                _byOpId.Remove(old.OpId);
            }
        }
    }

    /// <summary>Latest pending (queued/preparing) operation from any phone that targets <paramref name="piece"/>.</summary>
    public WebOp? PendingFor(DrumPiece piece)
    {
        lock (_gate)
        {
            WebOp? best = null;
            foreach (var q in _perDevice.Values)
                foreach (var w in q)
                    if (w.IsPending && w.Targets(piece) && (best is null || w.CreatedAt >= best.CreatedAt)) best = w;
            return best;
        }
    }
}

/// <summary>At most <see cref="Limit"/> commands per device per second. Thread-safe.</summary>
internal sealed class CommandLimiter(TimeProvider time)
{
    public const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    /// <summary>True when allowed (and counted); otherwise the time until the next slot.</summary>
    public bool TryAcquire(string deviceKey, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_hits.TryGetValue(deviceKey, out var q)) _hits[deviceKey] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
            if (q.Count >= Limit) { retryAfter = q.Peek() + Window - now; return false; }
            q.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
