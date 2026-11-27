using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.Remote.Tests;

/// <summary>
/// End-to-end tests against the REAL Kestrel panel server (127.0.0.1, free port) while a simulated engine processes
/// audio continuously (docs/painel-api.md). The swap run reuses the harness' recorded-returns checks (SwapHarness).
/// </summary>
public class E2EPanelTests
{
    private static string NewOp() => Guid.NewGuid().ToString();

    [Fact]
    public async Task PairStateSwapPieceThenPresetWhileAudioRuns_RecordedReturnsVerified()
    {
        await using var rig = await E2EPanelRig.StartAsync(speed: 4.0);
        using var phone = rig.Phone();
        await phone.PairAsync();
        var state = await phone.GetJson("api/state");
        Assert.True((bool)state["simulation"]!);
        Assert.True((bool)state["audioRunning"]!);
        Assert.Equal("demo-rock", (string?)state["kit"]!["presetId"]);
        var samples = (await phone.GetJson("api/samples")).AsArray();
        Assert.Contains(samples, s => (string?)s!["id"] == "bumbos/bumbo-seco.wav");
        var presets = (await phone.GetJson("api/presets")).AsArray();
        Assert.False((bool)presets.Single(p => (string?)p!["id"] == SimulationSampleLibrary.BrokenPresetId)!["available"]!);

        var sc = rig.Scenario;
        var o = new SwapOptions();
        var ops = new List<SwapOperationRecord>();
        async Task Step(string step, string desc, double atSec, string path, object body, bool expectFail)
        {
            Assert.True(rig.Audio.WaitForPosition((long)(atSec * sc.SampleRate), TimeSpan.FromSeconds(30)));
            long block = rig.Audio.Position / rig.Audio.BlockSize;
            var before = rig.Engine.ActiveKit;
            var r = await phone.Post(path, body);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            string opId = (string)(await PhoneClient.Body(r))["opId"]!;
            var op = await phone.WaitOperation(opId, requireConfirmed: !expectFail);
            Assert.Equal(expectFail ? "failed" : "applied", (string)op["status"]!);
            var snap = rig.Controller.GetOperation(Guid.Parse(opId))!;
            ops.Add(new SwapOperationRecord(step, desc, atSec, block, snap, before, rig.Engine.ActiveKit));
            if (expectFail)
            {
                var err = Assert.Single(op["errors"]!.AsArray());
                Assert.Equal("snare", (string?)err!["piece"]);
                Assert.Equal("file_not_found", (string?)err["code"]);
            }
            else Assert.True((bool)op["engineConfirmed"]!);
        }

        await Step("peça", "kick → bumbo-seco (HTTP)", o.SingleSwapAtSec, "api/pieces/kick/sample",
            new { sampleId = o.SingleSampleId, clientOpId = NewOp(), baseVersion = await phone.Version() }, false);
        await Step("preset", "demo-suave (HTTP)", o.PresetAtSec, "api/kit/preset",
            new { presetId = o.PresetId, clientOpId = NewOp(), baseVersion = await phone.Version() }, false);
        long versionBeforeBroken = await phone.Version();
        await Step("preset-com-erro", "demo-com-erro (HTTP)", o.BrokenPresetAtSec, "api/kit/preset",
            new { presetId = SimulationSampleLibrary.BrokenPresetId, clientOpId = NewOp(), baseVersion = versionBeforeBroken }, true);
        Assert.Equal(versionBeforeBroken, await phone.Version());               // failed preset: state unchanged

        Assert.True(rig.Audio.WaitForPosition(sc.Length, TimeSpan.FromSeconds(30)));
        rig.Audio.Stop();
        var cap = rig.Audio.GetCapture();
        var rec = new SwapRecording
        {
            Scenario = sc, Mode = "E2E: servidor Kestrel real + laço de áudio simulado (4x)", BlockSize = rig.Audio.BlockSize,
            ByPiece = cap.ByPiece.Select(a => a.Length >= sc.Length ? a[..sc.Length] : a.Concat(new float[sc.Length - a.Length]).ToArray()).ToArray(),
            Events = rig.SnapshotEvents().Where(e => e.Event.PlaybackSampleIndex < sc.Length).ToList(),
            BlockKits = cap.Kits, BlockGenerations = cap.Generations, Operations = ops, Callbacks = cap.Kits.Length,
            RequireRingingAtSwap = false, // 4x + HTTP latency: reported, verified by the residual check
        };
        var rep = SwapHarness.Analyze(rec, o);
        Assert.True(rep.Passed, string.Join("\n", rep.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));

        var final = await phone.GetJson("api/state");
        Assert.Equal("demo-suave", (string?)final["kit"]!["presetId"]);
        Assert.Equal("bumbos/bumbo-grave.wav", (string?)final["pieces"]!.AsArray().Single(p => (string?)p!["piece"] == "kick")!["sample"]!["id"]);
    }

    [Fact]
    public async Task InvalidOrMissingToken_IsNotPaired()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var none = rig.Phone();
        var r1 = await none.Http.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        Assert.Equal("not_paired", (string?)(await PhoneClient.Body(r1))["code"]);

        using var forged = rig.Phone();
        forged.Cookies.Add(new Uri(rig.Origin), new Cookie("sd_device", new string('A', 43)));
        var r2 = await forged.Http.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        var r3 = await forged.Post("api/kit/preset", new { presetId = "demo-rock", clientOpId = NewOp(), baseVersion = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);

        var wrong = await none.Post("api/pair", new { code = "000000" == rig.Server.Pairing.Code ? "111111" : "000000", deviceName = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("pairing_invalid", (string?)(await PhoneClient.Body(wrong))["code"]);
    }

    [Fact]
    public async Task RevokedDevice_SseGetsRevokedAndCloses_ThenNotPaired()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync("Celular a remover");
        await using var sse = await SseReader.OpenAsync(phone);
        Assert.Equal(HttpStatusCode.OK, sse.Status);
        var first = await sse.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("state", first?.Event);

        var device = Assert.Single(rig.Server.Devices.List());
        Assert.True(rig.Server.Devices.Remove(device.Key));
        var (revoked, seen) = await sse.UntilAsync("revoked", TimeSpan.FromSeconds(5));
        Assert.True(revoked is not null, "eventos vistos: " + string.Join(",", seen));
        Assert.Null(await sse.NextAsync(TimeSpan.FromSeconds(5)));            // stream closed by the server
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Http.GetAsync("api/state")).StatusCode);
    }

    [Fact]
    public async Task SseReconnect_GetsFullCurrentState_AndNothingIsReplayed()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync();
        string? lastId;
        await using (var sse = await SseReader.OpenAsync(phone))
        {
            var s = await sse.NextAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("state", s?.Event);
            lastId = s?.Id;
            // a phone operation while connected (its `op` events go to this connection only)
            var r = await phone.Post("api/pieces/snare/sample", new { sampleId = "caixas/caixa-aro.wav", clientOpId = NewOp(), baseVersion = await phone.Version() });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            await phone.WaitOperation((string)(await PhoneClient.Body(r))["opId"]!, requireConfirmed: true);
        }
        // while disconnected, the PC changes the kit
        var pc = await rig.Controller.SetPieceSample(DrumPiece.Tom, "tons/tom-curto.wav").Completion;
        Assert.Equal(KitOperationStatus.Applied, pc.Status);
        long current = rig.Controller.Version;

        await using var again = await SseReader.OpenAsync(phone, lastEventId: lastId);
        var first = await again.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("state", first?.Event);
        var st = JsonNode.Parse(first!.Value.Data)!;
        Assert.True((long)st["version"]! >= current);
        Assert.Equal("tons/tom-curto.wav", (string?)st["pieces"]!.AsArray().Single(p => (string?)p!["piece"] == "tom")!["sample"]!["id"]);
        Assert.Equal("caixas/caixa-aro.wav", (string?)st["pieces"]!.AsArray().Single(p => (string?)p!["piece"] == "snare")!["sample"]!["id"]);
        var (op, seen) = await again.UntilAsync("op", TimeSpan.FromSeconds(1.5));
        Assert.True(op is null, "evento op antigo reenviado: " + string.Join(",", seen));
    }

    [Fact]
    public async Task ConcurrentPcAndPhone_StaleSelectionIsRejected_OtherPieceIsNot()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync();
        long seen = await phone.Version();                                     // what the phone shows

        // the PC (same KitController) changes the kick after that
        Assert.Equal(KitOperationStatus.Applied, (await rig.Controller.SetPieceSample(DrumPiece.Kick, "bumbos/bumbo-grave.wav").Completion).Status);
        var kickBefore = rig.Engine.ActiveKit![DrumPiece.Kick].Samples;

        var stale = await phone.Post("api/pieces/kick/sample", new { sampleId = "bumbos/bumbo-seco.wav", clientOpId = NewOp(), baseVersion = seen });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await PhoneClient.Body(stale);
        Assert.Equal("stale_state", (string?)body["code"]);
        Assert.Equal("bumbos/bumbo-grave.wav", (string?)body["state"]!["pieces"]!.AsArray().Single(p => (string?)p!["piece"] == "kick")!["sample"]!["id"]);
        Assert.Same(kickBefore, rig.Engine.ActiveKit![DrumPiece.Kick].Samples); // the old selection did not overwrite

        var presetStale = await phone.Post("api/kit/preset", new { presetId = "demo-suave", clientOpId = NewOp(), baseVersion = seen });
        Assert.Equal(HttpStatusCode.Conflict, presetStale.StatusCode);

        // a different piece is not stale for the same baseVersion
        var other = await phone.Post("api/pieces/tom/sample", new { sampleId = "tons/tom-agudo.wav", clientOpId = NewOp(), baseVersion = seen });
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        Assert.Equal("applied", (string?)(await phone.WaitOperation((string)(await PhoneClient.Body(other))["opId"]!, true))["status"]);
    }

    [Fact]
    public async Task SameClientOpId_ReturnsTheSameOperation()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync();
        string cop = NewOp();
        long v = await phone.Version();
        var a = await phone.Post("api/pieces/snare/sample", new { sampleId = "caixas/caixa-aberta.wav", clientOpId = cop, baseVersion = v });
        var b = await phone.Post("api/pieces/snare/sample", new { sampleId = "caixas/caixa-aberta.wav", clientOpId = cop, baseVersion = v });
        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.Equal((string?)(await PhoneClient.Body(a))["opId"], (string?)(await PhoneClient.Body(b))["opId"]);
        var reused = await phone.Post("api/pieces/snare/sample", new { sampleId = "caixas/caixa-seca.wav", clientOpId = cop, baseVersion = v });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("client_op_reused", (string?)(await PhoneClient.Body(reused))["code"]);
    }

    [Fact]
    public async Task PanelStop_AudioKeepsProcessing_KitUnchanged()
    {
        await using var rig = await E2EPanelRig.StartAsync(speed: 1.0);
        using var phone = rig.Phone();
        await phone.PairAsync();
        Assert.True(rig.Audio.WaitForPosition(48000 / 4, TimeSpan.FromSeconds(10)));
        var kit = rig.Engine.ActiveKit;
        long callbacks = rig.Audio.Callbacks;
        await rig.Server.StopAsync();
        Assert.False(rig.Server.IsRunning);
        long afterStop = rig.Audio.Callbacks;
        await Task.Delay(400);
        Assert.True(rig.Audio.Callbacks > afterStop + 10, $"callbacks {callbacks} → {afterStop} → {rig.Audio.Callbacks}");
        Assert.True(rig.Audio.IsRunning);
        Assert.Same(kit, rig.Engine.ActiveKit);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => phone.Http.GetAsync("api/state"));
    }

    [Fact]
    public async Task HostOriginContentTypeAndCors_AreEnforced()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync();
        async Task<(HttpStatusCode, string?)> Send(HttpRequestMessage req)
        {
            var r = await phone.Http.SendAsync(req);
            string text = await r.Content.ReadAsStringAsync();
            string? code = text.StartsWith('{') ? (string?)JsonNode.Parse(text)!["code"] : null;
            return (r.StatusCode, code);
        }
        var badHost = new HttpRequestMessage(HttpMethod.Get, "api/state");
        badHost.Headers.Host = $"evil.example:{rig.Port}";
        Assert.Equal((HttpStatusCode.Forbidden, "host_not_allowed"), await Send(badHost));

        var body = new { presetId = "demo-rock", clientOpId = NewOp(), baseVersion = 0 };
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.Post("api/kit/preset", body, origin: null)).StatusCode);
        var evil = await phone.Post("api/kit/preset", body, origin: "http://evil.example");
        Assert.Equal("origin_not_allowed", (string?)(await PhoneClient.Body(evil))["code"]);

        var text = new HttpRequestMessage(HttpMethod.Post, "api/kit/preset") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain") };
        text.Headers.Add("Origin", rig.Origin);
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, "unsupported_media_type"), await Send(text));

        var options = new HttpRequestMessage(HttpMethod.Options, "api/kit/preset");
        options.Headers.Add("Origin", "http://evil.example");
        options.Headers.Add("Access-Control-Request-Method", "POST");
        var pre = await phone.Http.SendAsync(options);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, pre.StatusCode);
        Assert.DoesNotContain(pre.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));

        var pathy = await phone.Post("api/pieces/kick/sample", new { sampleId = "../settings.json", clientOpId = NewOp(), baseVersion = await phone.Version() });
        Assert.Equal(HttpStatusCode.BadRequest, pathy.StatusCode);
    }

    [Fact]
    public async Task PhoneTest_IsOffByDefault()
    {
        await using var rig = await E2EPanelRig.StartAsync();
        using var phone = rig.Phone();
        await phone.PairAsync();
        var r = await phone.Post("api/pieces/kick/test", new { clientOpId = NewOp() });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("test_not_allowed", (string?)(await PhoneClient.Body(r))["code"]);
    }
}
