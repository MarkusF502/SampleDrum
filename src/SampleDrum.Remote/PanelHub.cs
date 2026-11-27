namespace SampleDrum.Remote;

/// <summary>SSE hub (skeleton): full state on connect and on every change, op/meters/hit events, heartbeat.</summary>
internal sealed class PanelHub : IDisposable
{
    private readonly PanelRuntime _runtime;

    public PanelHub(PanelRuntime runtime) => _runtime = runtime;

    /// <summary>Enqueues a fresh `state` for every connection (never blocks the caller).</summary>
    public void BroadcastState() { }

    public void Start() { }

    public void Dispose() { }
}
