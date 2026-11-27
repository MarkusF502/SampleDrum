using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.Remote.Tests;

/// <summary>
/// End-to-end rig: the REAL Kestrel panel server on 127.0.0.1 (free port), the app's single KitController over the
/// --simulation demo library, a KitEngine processed continuously by <see cref="SimulatedAudioLoop"/>, and a sampler
/// that drains the engine's events/meters into <see cref="PanelTelemetry"/> (like the app's single sampler).
/// </summary>
public sealed class E2EPanelRig : IAsyncDisposable
{
    public SimulationSampleLibrary Library { get; }
    public KitScenario Scenario { get; }
    public KitEngine Engine { get; }
    public KitController Controller { get; }
    public PanelServer Server { get; }
    public SimulatedAudioLoop Audio { get; }
    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public List<KitEvent> Events { get; } = [];
    private readonly object _eventsGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sampler;
    private readonly string _root;

    private E2EPanelRig(string root, SimulationSampleLibrary lib, KitScenario sc, KitEngine engine, KitController controller,
        PanelServer server, SimulatedAudioLoop audio, int port)
    {
        _root = root; Library = lib; Scenario = sc; Engine = engine; Controller = controller; Server = server; Audio = audio; Port = port;
        _sampler = Task.Run(SamplerAsync);
    }

    public static async Task<E2EPanelRig> StartAsync(double speed = 4.0, bool allowPhoneTest = false, bool audioRunning = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "sd-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var lib = SimulationSampleLibrary.Create(root);
        var sc = KitScenario.Generate(1);
        var engine = SwapHarness.CreateEngine(sc);
        var controller = new KitController(engine, new SampleLibrary(lib.SamplesFolder));
        var initial = await controller.ApplyPreset(lib.Preset("demo-rock")).Completion;
        Assert.Equal(KitOperationStatus.Applied, initial.Status);
        var server = new PanelServer(new PanelBackend
        {
            Controller = controller, Engine = engine, PresetsFolder = () => lib.PresetsFolder, Devices = new DeviceRegistry(),
            RequestTest = p => engine.RequestTest(p),
        });
        server.AppStatus = new PanelAppStatus
        {
            ProfileName = RoutingProfiles.SimulationName, Simulation = true, AudioRunning = audioRunning,
            AllowPhoneTest = allowPhoneTest, Routed = [true, true, true, true],
        };
        int port = 0;
        for (int attempt = 0; ; attempt++)
        {
            port = FreePort();
            try { await server.StartAsync(new PanelOptions { Address = IPAddress.Loopback, Port = port }); break; }
            catch (PanelStartException) when (attempt < 5) { }
        }
        var audio = new SimulatedAudioLoop(engine, sc.Mics, 256, speed, sc.Length / (double)sc.SampleRate);
        if (audioRunning) audio.Start();
        return new E2EPanelRig(root, lib, sc, engine, controller, server, audio, port);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p is >= 1024 and <= 65535 ? p : FreePort();
    }

    /// <summary>Single consumer of the engine's events/meters, fanned out to the panel telemetry (like the app).</summary>
    private async Task SamplerAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            while (Engine.TryDequeueEvent(out var e))
            {
                lock (_eventsGate) Events.Add(e);
                Server.Backend.Telemetry.PublishHit(e.Piece, e.Event.Velocity, e.Event.IsManualTest);
            }
            foreach (var p in DrumPieces.All) Server.Backend.Telemetry.PublishPeak(p, Engine.ReadAndResetInputPeak(p));
            try { await Task.Delay(33, _cts.Token); } catch (OperationCanceledException) { }
        }
    }

    public List<KitEvent> SnapshotEvents() { lock (_eventsGate) return [.. Events]; }

    /// <summary>New HTTP client with its own cookie jar (one "phone").</summary>
    public PhoneClient Phone() => new(this);

    public async ValueTask DisposeAsync()
    {
        Audio.Stop();
        _cts.Cancel();
        try { await _sampler; } catch { }
        await Server.DisposeAsync();
        await Controller.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>One phone: HttpClient with its own cookies; POSTs carry Origin + application/json like the page.</summary>
public sealed class PhoneClient : IDisposable
{
    private readonly E2EPanelRig _rig;
    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }

    public PhoneClient(E2EPanelRig rig)
    {
        _rig = rig;
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, UseCookies = true })
        {
            BaseAddress = new Uri(rig.Origin + "/"), Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public Task<HttpResponseMessage> Post(string path, object body, string? origin = "default")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        if (origin is not null) req.Headers.Add("Origin", origin == "default" ? _rig.Origin : origin);
        return Http.SendAsync(req);
    }

    public async Task PairAsync(string name = "Celular de teste")
    {
        var r = await Post("api/pair", new { code = _rig.Server.Pairing.Code, deviceName = name });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    public async Task<JsonNode> GetJson(string path)
    {
        var r = await Http.GetAsync(path);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{path}: {(int)r.StatusCode} {body}");
        return JsonNode.Parse(body)!;
    }

    public async Task<long> Version() => (long)(await GetJson("api/state"))["version"]!;

    public static async Task<JsonNode> Body(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    /// <summary>Polls GET /api/operations/{opId} until terminal (and engine-confirmed when applied with audio running).</summary>
    public async Task<JsonNode> WaitOperation(string opId, bool requireConfirmed, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        JsonNode? op = null;
        while (DateTime.UtcNow < limit)
        {
            op = await GetJson($"api/operations/{opId}");
            string status = (string)op["status"]!;
            bool confirmed = (bool?)op["engineConfirmed"] ?? false;
            if (status is "failed" or "superseded") return op;
            if (status == "applied" && (!requireConfirmed || confirmed)) return op;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Operação {opId} não terminou: {op?.ToJsonString()}");
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>Minimal SSE reader for GET /api/events.</summary>
public sealed class SseReader : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private SseReader(HttpResponseMessage r, StreamReader reader) { _response = r; _reader = reader; }

    public HttpStatusCode Status => _response.StatusCode;

    public static async Task<SseReader> OpenAsync(PhoneClient phone, string? lastEventId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "api/events");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null) req.Headers.Add("Last-Event-ID", lastEventId);
        var r = await phone.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        return new SseReader(r, new StreamReader(await r.Content.ReadAsStreamAsync()));
    }

    /// <summary>Next event (name, id, data), skipping comments/retry; null when the stream ended or timed out.</summary>
    public async Task<(string Event, string? Id, string Data)?> NextAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string evt = "message"; string? id = null; var data = new StringBuilder();
        try
        {
            while (true)
            {
                string? line = await _reader.ReadLineAsync(cts.Token);
                if (line is null) return null;
                if (line.Length == 0)
                {
                    if (data.Length > 0 || evt != "message") return (evt, id, data.ToString());
                    continue;
                }
                if (line.StartsWith(':')) continue;
                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? "" : line[(colon + 1)..].TrimStart();
                switch (field)
                {
                    case "event": evt = value; break;
                    case "id": id = value; break;
                    case "data": if (data.Length > 0) data.Append('\n'); data.Append(value); break;
                }
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Reads until an event with the given name (or null at end/timeout); returns the skipped ones too.</summary>
    public async Task<((string Event, string? Id, string Data)? Hit, List<string> Seen)> UntilAsync(string name, TimeSpan timeout)
    {
        var seen = new List<string>();
        var limit = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < limit)
        {
            var e = await NextAsync(limit - DateTime.UtcNow);
            if (e is null) return (null, seen);
            seen.Add(e.Value.Event);
            if (e.Value.Event == name) return (e, seen);
        }
        return (null, seen);
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
