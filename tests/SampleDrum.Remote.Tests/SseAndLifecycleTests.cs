using System.Net;
using System.Net.Sockets;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote.Tests;

public class SseAndLifecycleTests
{
    [Fact]
    public async Task Events_StartWithFullState_ThenStateOpMetersAndHits()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        using var c = await h.PairedClientAsync();
        await using var sse = await c.EventsAsync();
        Assert.Equal("text/event-stream", sse.Response.Content.Headers.ContentType?.MediaType);
        var first = await sse.NextAsync();
        Assert.Equal("retry: 3000", sse.RawLines[0]);
        Assert.Equal("state", first!.Event);
        Assert.Equal("0", first.Id);
        Assert.Equal(0, first.Json<StateDto>().Version);

        // A change from the PC reaches the phone as a full state with id = version.
        await h.Controller.SetPieceSample(DrumPiece.Kick, "kick-a.wav").Completion;
        var st = await sse.NextAsync("state");
        Assert.Equal("1", st!.Id);
        Assert.Equal("kick-a.wav", st.Json<StateDto>().Pieces[0].Sample!.Id);

        // The phone's own command: op events up to engine confirmation.
        var r = await c.SetSampleAsync("snare", "caixas/caixa-1.wav", 1);
        string opId = (await r.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId;
        OperationDto? op = null;
        for (int i = 0; i < 20 && op?.EngineConfirmed != true; i++)
        {
            var e = await sse.NextAsync("op");
            op = e!.Json<OperationDto>();
            Assert.Equal(opId, op.OpId);
        }
        Assert.True(op!.EngineConfirmed);
        Assert.Equal("applied", op.Status);

        // Meters (~5 Hz) from the app's sampler fan-out, and hits.
        h.Telemetry.PublishPeak(DrumPiece.Kick, 0.5f);
        var meters = await sse.NextAsync("meters");
        var values = meters!.Json<Dictionary<string, float>>();
        Assert.Equal(["kick", "snare", "tom", "floorTom"], values.Keys);
        Assert.InRange(values["kick"], -90f, 0f);
        h.Telemetry.PublishHit(DrumPiece.Snare, 0.7f, false);
        var hit = (await sse.NextAsync("hit"))!.Json<HitDto>();
        Assert.Equal("snare", hit.Piece);
        Assert.Equal(0.7f, hit.Velocity, 3);
        Assert.False(hit.Test);
    }

    [Fact]
    public async Task OpEvents_GoOnlyToTheDeviceThatAsked()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var a = await h.PairedClientAsync("A");
        using var b = await h.PairedClientAsync("B");
        await using var sseB = await b.EventsAsync();
        await sseB.NextAsync("state");
        var r = await a.SetSampleAsync("kick", "kick-a.wav", 0);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        // B gets the new state but never A's op events.
        var seen = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var e = await sseB.NextAsync(3000);
            if (e is null) break;
            seen.Add(e.Event);
            if (e.Event == "state" && e.Json<StateDto>().Version == 1) break;
        }
        Assert.Contains("state", seen);
        Assert.DoesNotContain("op", seen);
    }

    [Fact]
    public async Task Reconnect_GetsTheFullCurrentState_NoReplay()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        string lastId;
        await using (var sse = await c.EventsAsync())
            lastId = (await sse.NextAsync("state"))!.Id!;
        // While the phone was offline: two changes (one of them its own, finished meanwhile).
        await h.Controller.SetPieceSample(DrumPiece.Kick, "kick-b.wav").Completion;
        var mine = await c.SetSampleAsync("snare", "caixas/caixa-2.wav", 1);
        await c.WaitOperationAsync((await mine.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId, o => o.Status == "applied");
        await using var again = await c.EventsAsync(lastEventId: lastId);
        var first = await again.NextAsync();
        Assert.Equal("state", first!.Event);                         // full state first, not replayed events
        var state = first.Json<StateDto>();
        Assert.Equal(2, state.Version);
        Assert.Equal("kick-b.wav", state.Pieces[0].Sample!.Id);
        Assert.Equal("caixas/caixa-2.wav", state.Pieces[1].Sample!.Id);
        Assert.Equal(2, h.Controller.Version);                       // nothing was re-sent by the server
    }

    [Fact]
    public async Task SseConnections_AreBoundedPerDevice()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var open = new List<SseStream>();
        try
        {
            for (int i = 0; i < PanelHub.MaxConnectionsPerDevice; i++)
            {
                var s = await c.EventsAsync();
                Assert.Equal(HttpStatusCode.OK, s.Response.StatusCode);
                await s.NextAsync("state");
                open.Add(s);
            }
            var extra = await c.EventsAsync();
            Assert.Equal((HttpStatusCode)429, extra.Response.StatusCode);
            Assert.Equal("too_many_connections", (await extra.Response.ProblemAsync()).Code);
            await extra.DisposeAsync();
        }
        finally { foreach (var s in open) await s.DisposeAsync(); }
    }

    [Fact]
    public async Task StateChangedHandler_NeverBlocksTheControllerWorker_WithASlowPhone()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        await using var sse = await c.EventsAsync();                 // never read: a stalled phone
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 300 state changes, each awaited (gains of one piece would supersede each other if queued together).
        for (int i = 0; i < 300; i++) await h.Controller.SetPieceGain(DrumPiece.Kick, -(i % 10)).Completion;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"controller took {sw.Elapsed}");
        Assert.Equal(300, h.Controller.Version);
    }

    [Fact]
    public async Task StoppingThePanel_ClosesPhones_AudioAndKitContinue()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        using var c = await h.PairedClientAsync();
        var r = await c.PresetAsync("rock", 0);
        await c.WaitOperationAsync((await r.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId, o => o.EngineConfirmed);
        var kit = h.Engine.ActiveKit;
        await using var sse = await c.EventsAsync();
        await sse.NextAsync("state");

        var stop = h.Server.StopAsync();
        Assert.True(await Task.WhenAny(stop, Task.Delay(5000)) == stop, "StopAsync hung");
        Assert.False(h.Server.IsRunning);
        Assert.Null(await sse.NextAsync("state", 5000));              // the stream ended
        long before = h.Engine.SamplesProcessed;
        await Task.Delay(100);
        Assert.True(h.Engine.SamplesProcessed > before, "audio loop stopped with the panel");
        Assert.Same(kit, h.Engine.ActiveKit);
        Assert.True(h.AudioAlive);
        // The controller keeps working for the PC.
        Assert.Equal(KitOperationStatus.Applied, (await h.Controller.SetPieceGain(DrumPiece.Kick, -1).Completion).Status);
        // And the panel can start again; paired devices persist across restarts.
        await h.Server.StartAsync(new PanelOptions { Port = PanelTestHost.Port });
        using var again = h.Client();
        again.Cookie = c.Cookie;
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task PortInUse_ThrowsAClearPtBrError_AndAnotherPortWorks()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        int busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            if (busy < 1024) return;   // ephemeral ports are >= 1024 on Windows; guard anyway
            var dir = Directory.CreateTempSubdirectory("sd-port-");
            var engine = new KitEngine(48000, KitSettings.Default);
            await using var controller = new KitController(engine, new SampleLibrary(dir.FullName));
            await using var server = new PanelServer(new PanelBackend
            {
                Controller = controller, Engine = engine, PresetsFolder = () => dir.FullName, Devices = new DeviceRegistry(),
            });
            var ex = await Assert.ThrowsAsync<PanelStartException>(() => server.StartAsync(new PanelOptions { Port = busy }));
            Assert.Contains("em uso", ex.Message);
            Assert.Contains(busy.ToString(), ex.Message);
            Assert.False(server.IsRunning);

            // Real Kestrel on a free port: page reachable on loopback, Host checked.
            var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            int port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            await server.StartAsync(new PanelOptions { Port = port });
            Assert.True(server.IsRunning);
            Assert.Equal($"http://127.0.0.1:{port}/", server.Url);
            using var http = new HttpClient();
            var page = await http.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var state = await http.GetAsync($"http://127.0.0.1:{port}/api/state");
            Assert.Equal(HttpStatusCode.Unauthorized, state.StatusCode);
            await server.StopAsync();
            Assert.ThrowsAny<Exception>(() => http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult());
            dir.Delete(true);
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public async Task InvalidPort_IsRefusedBeforeBinding()
    {
        await using var h = await PanelTestHost.StartAsync();
        await h.Server.StopAsync();
        var ex = await Assert.ThrowsAsync<PanelStartException>(() => h.Server.StartAsync(new PanelOptions { Port = 80 }));
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public async Task EmbeddedPage_IsServed_WithEtag()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("<html", await r.Content.ReadAsStringAsync());
        var etag = r.Headers.ETag!.Tag;
        var req = c.Request(HttpMethod.Get, "/");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
        foreach (var path in StaticAssets.Paths.Where(p => p != "index.html"))
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/assets/" + path)).StatusCode);
    }
}
