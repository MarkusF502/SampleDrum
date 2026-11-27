using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

/// <summary>
/// SSE fan-out. Every connection has a small bounded queue (oldest dropped when a slow phone falls behind) and one
/// writer task (the request itself). Producers (controller worker, app sampler, timer) only TryWrite: nothing here
/// ever blocks the controller worker thread or the UI thread, and the hub never reads the engine queues.
/// </summary>
internal sealed class PanelHub : IDisposable
{
    public const int MaxConnectionsPerDevice = 4;
    public const int MaxConnections = 32;
    public static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    /// <summary>Applied operations still waiting for audio confirmation are watched at most this long.</summary>
    public static readonly TimeSpan ConfirmationWatch = TimeSpan.FromMinutes(10);

    private readonly PanelRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<Connection> _connections = [];
    private readonly List<WebOp> _watched = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _ticker;
    private bool _disposed;

    public PanelHub(PanelRuntime runtime) => _runtime = runtime;

    public int ConnectionCount { get { lock (_gate) return _connections.Count; } }

    public void Start() => _ticker = Task.Run(TickAsync);

    /// <summary>Queues a fresh `state` for every connection (each writer serializes the CURRENT state).</summary>
    public void BroadcastState()
    {
        foreach (var c in Snapshot()) c.Queue.Writer.TryWrite(SseItem.StateMarker);
    }

    /// <summary>Controller StateChanged (worker thread): state + op updates. Never waits.</summary>
    public void OnControllerChanged()
    {
        BroadcastState();
        PushOperationUpdates();
    }

    public void OnHit(DrumPiece piece, float velocity, bool test)
    {
        var conns = Snapshot();
        if (conns.Length == 0) return;
        var item = new SseItem("hit", JsonSerializer.Serialize(new HitDto(Wire.PieceId(piece), MathF.Round(velocity, 3), test), Wire.Json));
        foreach (var c in conns) c.Queue.Writer.TryWrite(item);
    }

    /// <summary>Watches an accepted operation and pushes `op` events to its device on every change.</summary>
    public void Watch(WebOp op)
    {
        lock (_gate) _watched.Add(op);
        PushOperationUpdates();
    }

    /// <summary>Revocation/logout: those connections get `revoked` and are closed now.</summary>
    public void Revoke(string deviceKey)
    {
        Connection[] mine;
        lock (_gate) mine = _connections.Where(c => c.DeviceKey == deviceKey).ToArray();
        foreach (var c in mine) c.Close(revoked: true);
    }

    /// <summary>GET /api/events.</summary>
    public async Task HandleAsync(HttpContext ctx, PairedDevice device)
    {
        var conn = new Connection(device.Key, device.Name);
        lock (_gate)
        {
            if (_disposed) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            if (_connections.Count >= MaxConnections || _connections.Count(c => c.DeviceKey == device.Key) >= MaxConnectionsPerDevice)
                conn = null!;
            else _connections.Add(conn);
        }
        if (conn is null)
        {
            await PanelRuntime.WriteProblem(ctx, 429, "too_many_connections", "Conexões demais",
                $"No máximo {MaxConnectionsPerDevice} conexões ao vivo por aparelho (feche outras abas).");
            return;
        }
        try
        {
            var res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers.CacheControl = "no-store";
            res.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, conn.Closed.Token, _stop.Token);
            var ct = linked.Token;
            // Last-Event-ID is deliberately ignored: a (re)connection always starts from the full state, no replay.
            await WriteAsync(res, "retry: 3000\n\n", ct);
            await WriteStateAsync(res, conn, ct);
            var reader = conn.Queue.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var item))
                    {
                        if (ReferenceEquals(item, SseItem.StateMarker)) await WriteStateAsync(res, conn, ct, skipSame: true);
                        else if (ReferenceEquals(item, SseItem.Heartbeat)) await WriteAsync(res, ": ping\n\n", ct);
                        else await WriteAsync(res, Format(item.Event, item.Data, null), ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            if (conn.Revoked && !ctx.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await WriteAsync(res, Format("revoked", "{\"reason\":\"revoked\"}", null), t.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            lock (_gate) _connections.Remove(conn);
            conn.Dispose();
        }
    }

    private async Task WriteStateAsync(HttpResponse res, Connection conn, CancellationToken ct, bool skipSame = false)
    {
        var state = _runtime.BuildState(conn.DeviceName);
        string json = JsonSerializer.Serialize(state, Wire.Json);
        if (skipSame && json == conn.LastState) return;
        conn.LastState = json;
        await WriteAsync(res, Format("state", json, state.Version), ct);
    }

    private static string Format(string evt, string data, long? id)
    {
        var sb = new StringBuilder();
        if (id is { } v) sb.Append("id: ").Append(v).Append('\n');
        sb.Append("event: ").Append(evt).Append('\n');
        foreach (var line in data.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
        return sb.Append('\n').ToString();
    }

    private static async Task WriteAsync(HttpResponse res, string text, CancellationToken ct)
    {
        await res.WriteAsync(text, Encoding.UTF8, ct);
        await res.Body.FlushAsync(ct);
    }

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(MeterInterval);
        int ticks = 0;
        int heartbeatEvery = (int)(HeartbeatInterval / MeterInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    var peaks = _runtime.Server.Backend.Telemetry.TakePeaks();
                    var conns = Snapshot();
                    if (conns.Length > 0)
                    {
                        var meters = new Dictionary<string, float>();
                        foreach (var p in DrumPieces.All) meters[Wire.PieceId(p)] = PanelTelemetry.ToDbfs(peaks[(int)p]);
                        var item = new SseItem("meters", JsonSerializer.Serialize(meters, Wire.Json));
                        bool beat = ++ticks % heartbeatEvery == 0;
                        foreach (var c in conns)
                        {
                            c.Queue.Writer.TryWrite(item);
                            if (beat) c.Queue.Writer.TryWrite(SseItem.Heartbeat);
                        }
                    }
                    // Audio confirmation has no controller event: poll the watched operations here.
                    PushOperationUpdates();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _runtime.Server.Write($"AVISO: painel do celular: falha ao enviar medidores ({ex.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PushOperationUpdates()
    {
        List<(WebOp Op, string Json)>? send = null;
        var now = _runtime.Server.Backend.Time.GetUtcNow();
        lock (_gate)
        {
            for (int i = _watched.Count - 1; i >= 0; i--)
            {
                var w = _watched[i];
                var dto = _runtime.BuildOperation(w, w.Operation?.Snapshot);
                if (dto is null) { _watched.RemoveAt(i); continue; }
                string sig = $"{dto.Status}|{dto.EngineConfirmed}|{dto.AudioRunning}|{dto.ResultVersion}|{dto.Errors.Count}";
                if (sig != w.LastSignature)
                {
                    w.LastSignature = sig;
                    (send ??= []).Add((w, JsonSerializer.Serialize(dto, Wire.Json)));
                }
                bool done = dto.Status is "failed" or "superseded" || dto.EngineConfirmed || dto.Status == "applied" && w.Kind == WebOpKind.Test;
                if (done || now - w.CreatedAt > ConfirmationWatch) _watched.RemoveAt(i);
            }
        }
        if (send is null) return;
        var conns = Snapshot();
        foreach (var (op, json) in send)
        {
            var item = new SseItem("op", json);
            foreach (var c in conns)
                if (c.DeviceKey == op.DeviceKey) c.Queue.Writer.TryWrite(item);
        }
    }

    private Connection[] Snapshot() { lock (_gate) return _connections.ToArray(); }

    public void Dispose()
    {
        Connection[] all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _connections.ToArray();
        }
        _stop.Cancel();
        foreach (var c in all) c.Close(revoked: false);
        try { _ticker?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _stop.Dispose();
    }

    private sealed record SseItem(string Event, string Data)
    {
        public static readonly SseItem StateMarker = new("state", "");
        public static readonly SseItem Heartbeat = new("", "");
    }

    private sealed class Connection(string deviceKey, string deviceName) : IDisposable
    {
        public string DeviceKey { get; } = deviceKey;
        public string DeviceName { get; } = deviceName;
        public Channel<SseItem> Queue { get; } = Channel.CreateBounded<SseItem>(new BoundedChannelOptions(128)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public CancellationTokenSource Closed { get; } = new();
        public string? LastState { get; set; }
        public volatile bool Revoked;

        public void Close(bool revoked)
        {
            if (revoked) Revoked = true;
            Queue.Writer.TryComplete();
            try { if (!revoked) Closed.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose() => Closed.Dispose();
    }
}
