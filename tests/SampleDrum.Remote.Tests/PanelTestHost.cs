using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SampleDrum.Core;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote.Tests;

/// <summary>Manually advanced clock (pairing expiry / lockout windows).</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A panel server on an in-memory TestServer over a real controller + engine, with a temp sample library and presets
/// folder and an optional background "audio" loop that keeps calling Process (like a host).
/// </summary>
public sealed class PanelTestHost : IAsyncDisposable
{
    public const int Rate = 48000;
    public const int Port = 8750;
    public static readonly Uri Base = new($"http://127.0.0.1:{Port}/");

    private readonly CancellationTokenSource _audioStop = new();
    private Task? _audio;

    public string Dir { get; }
    public string Samples { get; }
    public string Presets { get; }
    public KitEngine Engine { get; }
    public KitController Controller { get; }
    public DeviceRegistry Devices { get; }
    public PanelTelemetry Telemetry { get; } = new();
    public PanelServer Server { get; }
    public ManualTime? Time { get; }
    public ConcurrentQueue<string> Logs { get; } = new();
    public ConcurrentQueue<DrumPiece> Tests { get; } = new();

    private PanelTestHost(ManualTime? time)
    {
        Time = time;
        Dir = Directory.CreateTempSubdirectory("sd-remote-").FullName;
        Samples = Path.Combine(Dir, "Samples");
        Presets = Path.Combine(Dir, "Presets");
        Directory.CreateDirectory(Path.Combine(Samples, "caixas"));
        Directory.CreateDirectory(Presets);
        Wav("kick-a.wav", 0.5f);
        Wav("kick-b.wav", 0.6f);
        Wav("caixas/caixa-1.wav", 0.4f);
        Wav("caixas/caixa-2.wav", 0.3f);
        File.WriteAllBytes(Path.Combine(Samples, "corrompido.wav"), Encoding.ASCII.GetBytes("RIFF????WAVEjunkjunkjunk"));
        Preset("rock", "Rock 1", "kick-a.wav", "caixas/caixa-1.wav");
        Preset("leve", "Leve", "kick-b.wav", "caixas/caixa-2.wav");
        Preset("quebrado", "Quebrado", "nao-existe.wav", "caixas/caixa-1.wav");

        Engine = new KitEngine(Rate, KitSettings.Default);
        Engine.ApplyKit(PreparedKit.Empty(Rate));
        Controller = new KitController(Engine, new SampleLibrary(Samples));
        Devices = new DeviceRegistry(null, time);
        Server = new PanelServer(new PanelBackend
        {
            Controller = Controller, Engine = Engine, PresetsFolder = () => Presets, Devices = Devices,
            Telemetry = Telemetry, Time = (TimeProvider?)time ?? TimeProvider.System,
            RequestTest = p => { Tests.Enqueue(p); Engine.RequestTest(p, 1f); },
        }) { ConfigureBuilder = b => b.WebHost.UseTestServer() };
        Server.Log += Logs.Enqueue;
        Server.AppStatus = new PanelAppStatus { ProfileName = "Igreja", Routed = [true, true, false, false] };
    }

    public static async Task<PanelTestHost> StartAsync(bool audio = false, ManualTime? time = null)
    {
        var h = new PanelTestHost(time);
        await h.Server.StartAsync(new PanelOptions { Port = Port, MachineName = "PC-IGREJA" });
        if (audio) h.StartAudio();
        return h;
    }

    public void Wav(string relative, float value, int length = 480) =>
        WavFile.WriteMono(Path.Combine(Samples, relative), Enumerable.Repeat(value, length).ToArray(), Rate);

    public void Preset(string id, string name, string kick, string snare) =>
        new KitPreset
        {
            Name = name,
            Pieces = new Dictionary<DrumPiece, PiecePreset>
            {
                [DrumPiece.Kick] = new(kick, GainDb: -2), [DrumPiece.Snare] = new(snare),
            },
        }.Save(Path.Combine(Presets, id + ".json"));

    /// <summary>Background host loop: 64-frame blocks, like an audio callback (the test's "audio is running").</summary>
    public void StartAudio()
    {
        Server.AppStatus = Server.AppStatus with { AudioRunning = true };
        _audio = Task.Factory.StartNew(() =>
        {
            var ins = DrumPieces.All.Select(_ => new float[64]).ToArray();
            var outs = DrumPieces.All.Select(_ => new float[64]).ToArray();
            while (!_audioStop.IsCancellationRequested)
            {
                foreach (var o in outs) Array.Clear(o);
                Engine.Process(ins, outs, 64);
                Thread.Sleep(1);
            }
        }, TaskCreationOptions.LongRunning);
    }

    public bool AudioAlive => _audio is { IsCompleted: false };

    public PanelClient Client(string? origin = "http://127.0.0.1:8750")
    {
        var http = Server.App!.GetTestServer().CreateClient();
        http.BaseAddress = Base;
        return new PanelClient(http, origin);
    }

    /// <summary>Paired client (cookie set from the pairing response).</summary>
    public async Task<PanelClient> PairedClientAsync(string name = "Celular teste")
    {
        var c = Client();
        var r = await c.PairAsync(Server.Pairing.Code, name);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        _audioStop.Cancel();
        if (_audio is not null) await _audio;
        await Server.DisposeAsync();
        await Controller.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

/// <summary>HttpClient wrapper with a manual cookie jar (TestServer has none) and the browser's Origin header.</summary>
public sealed class PanelClient(HttpClient http, string? origin) : IDisposable
{
    public HttpClient Http { get; } = http;
    public string? Cookie { get; set; }
    public string? SetCookieHeader { get; private set; }

    public HttpRequestMessage Request(HttpMethod method, string path, object? json = null, string? rawBody = null,
        string contentType = "application/json")
    {
        var req = new HttpRequestMessage(method, path);
        if (origin is not null && method == HttpMethod.Post) req.Headers.Add("Origin", origin);
        if (Cookie is not null) req.Headers.Add("Cookie", $"{PanelRuntime.CookieName}={Cookie}");
        if (json is not null || rawBody is not null)
            req.Content = new StringContent(rawBody ?? JsonSerializer.Serialize(json, Wire.Json), Encoding.UTF8, contentType);
        else if (method == HttpMethod.Post)
            req.Content = new StringContent("{}", Encoding.UTF8, contentType);
        return req;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        var r = await Http.SendAsync(req, opt);
        if (r.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var v in values.Where(v => v.StartsWith(PanelRuntime.CookieName + "=", StringComparison.Ordinal)))
            {
                SetCookieHeader = v;
                string value = v[(PanelRuntime.CookieName.Length + 1)..].Split(';')[0];
                Cookie = value.Length == 0 ? null : value;
            }
        return r;
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(Request(HttpMethod.Post, path, body));
    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(Request(HttpMethod.Get, path));

    public Task<HttpResponseMessage> PairAsync(string code, string name = "Celular teste") =>
        PostAsync("/api/pair", new { code, deviceName = name });

    public async Task<T> GetJsonAsync<T>(string path)
    {
        var r = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsyncSafe<T>())!;
    }

    public Task<StateDto> StateAsync() => GetJsonAsync<StateDto>("/api/state");

    public Task<HttpResponseMessage> SetSampleAsync(string piece, string sampleId, long baseVersion, string? clientOpId = null) =>
        PostAsync($"/api/pieces/{piece}/sample", new { sampleId, clientOpId = clientOpId ?? Guid.NewGuid().ToString(), baseVersion });

    public Task<HttpResponseMessage> PresetAsync(string presetId, long baseVersion, string? clientOpId = null) =>
        PostAsync("/api/kit/preset", new { presetId, clientOpId = clientOpId ?? Guid.NewGuid().ToString(), baseVersion });

    /// <summary>Polls GET /api/operations until <paramref name="until"/> holds (or times out).</summary>
    public async Task<OperationDto> WaitOperationAsync(string opId, Func<OperationDto, bool> until, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var op = await GetJsonAsync<OperationDto>($"/api/operations/{opId}");
            if (until(op)) return op;
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"operation {opId} stuck at {op.Status} confirmed={op.EngineConfirmed}");
            await Task.Delay(20);
        }
    }

    public async Task<SseStream> EventsAsync(string? lastEventId = null)
    {
        var req = Request(HttpMethod.Get, "/api/events");
        if (lastEventId is not null) req.Headers.Add("Last-Event-ID", lastEventId);
        var r = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        return new SseStream(r);
    }

    public void Dispose() => Http.Dispose();
}

public sealed record SseEvent(string Event, string? Id, string Data)
{
    public T Json<T>() => JsonSerializer.Deserialize<T>(Data, Wire.Json)!;
}

/// <summary>Reads SSE events from a streaming response.</summary>
public sealed class SseStream : IAsyncDisposable
{
    private readonly StreamReader? _reader;
    public HttpResponseMessage Response { get; }
    public List<string> Comments { get; } = [];
    public List<string> RawLines { get; } = [];

    public SseStream(HttpResponseMessage response)
    {
        Response = response;
        if (response.StatusCode == HttpStatusCode.OK)
            _reader = new StreamReader(response.Content.ReadAsStream());
    }

    /// <summary>Next event (skipping `retry:` and comments); null when the server closed the stream.</summary>
    public async Task<SseEvent?> NextAsync(int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        string? evt = null, id = null;
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try { line = await _reader!.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("no SSE event in time"); }
            if (line is null) return null;
            RawLines.Add(line);
            if (line.Length == 0)
            {
                if (evt is not null) return new SseEvent(evt, id, data.ToString());
                continue;
            }
            if (line.StartsWith(':')) { Comments.Add(line); continue; }
            if (line.StartsWith("event: ")) evt = line[7..];
            else if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("data: ")) { if (data.Length > 0) data.Append('\n'); data.Append(line[6..]); }
        }
    }

    /// <summary>Next event of the given name (others skipped).</summary>
    public async Task<SseEvent?> NextAsync(string name, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            int left = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
            var e = await NextAsync(left);
            if (e is null || e.Event == name) return e;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        Response.Dispose();
        await Task.CompletedTask;
    }
}

internal static class HttpExtensions
{
    public static async Task<T?> ReadFromJsonAsyncSafe<T>(this HttpContent content) =>
        JsonSerializer.Deserialize<T>(await content.ReadAsStringAsync(), Wire.Json);

    public static async Task<ProblemDto> ProblemAsync(this HttpResponseMessage r)
    {
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        return (await r.Content.ReadFromJsonAsyncSafe<ProblemDto>())!;
    }
}
