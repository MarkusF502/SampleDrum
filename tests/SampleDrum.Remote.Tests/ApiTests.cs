using System.Net;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote.Tests;

public class ApiTests
{
    private static async Task<string> OpIdAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await r.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId;
    }

    [Fact]
    public async Task Catalog_ListsLibrarySamplesAndPresets_ById()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var samples = await c.GetJsonAsync<SampleDto[]>("/api/samples");
        Assert.Equal(["caixa-1", "caixa-2", "kick-a", "kick-b"], samples.Select(s => s.Name));
        Assert.Contains(samples, s => s.Id == "caixas/caixa-1.wav" && s.DurationMs == 10 && s.SampleRate == 48000 && s.Channels == 1);
        Assert.DoesNotContain(samples, s => s.Id.Contains(h.Dir));                // never absolute paths

        var presets = await c.GetJsonAsync<PresetDto[]>("/api/presets");
        Assert.Equal(["leve", "quebrado", "rock"], presets.Select(p => p.Id));
        var rock = presets.Single(p => p.Id == "rock");
        Assert.Equal("Rock 1", rock.Name);
        Assert.True(rock.Available);
        Assert.Equal(["kick", "snare", "tom", "floorTom"], rock.Pieces.Keys);
        Assert.Equal("kick-a.wav", rock.Pieces["kick"].SampleId);
        Assert.Equal(-2, rock.Pieces["kick"].GainDb);
        Assert.Equal("caixas/caixa-1.wav", rock.Pieces["snare"].SampleId);
        Assert.Null(rock.Pieces["tom"].SampleId);
        Assert.False(presets.Single(p => p.Id == "quebrado").Available);
    }

    [Fact]
    public async Task PieceSwapAndPreset_GoThroughTheController_ConfirmedByTheRunningEngine()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        using var c = await h.PairedClientAsync();
        var s0 = await c.StateAsync();
        Assert.True(s0.AudioRunning);

        string op1 = await OpIdAsync(await c.SetSampleAsync("snare", "caixas/caixa-2.wav", s0.Version));
        var done1 = await c.WaitOperationAsync(op1, o => o.EngineConfirmed);
        Assert.Equal("applied", done1.Status);
        Assert.Equal("pieceSample", done1.Kind);
        Assert.Equal("snare", done1.Piece);
        Assert.Equal("caixas/caixa-2.wav", done1.SampleId);
        Assert.Empty(done1.Errors);
        Assert.Equal(s0.Version + 1, done1.ResultVersion);
        Assert.Equal("caixa-2", h.Engine.PlayingKit![DrumPiece.Snare].Name);           // the audio thread has it

        var s1 = await c.StateAsync();
        var snare = s1.Pieces.Single(p => p.Piece == "snare");
        Assert.Equal("caixas/caixa-2.wav", snare.Sample!.Id);
        Assert.Equal("caixa-2", snare.Sample.Name);
        Assert.Equal(10, snare.Sample.DurationMs);
        Assert.Equal(s1.Version, snare.SampleVersion);
        Assert.Null(s1.Kit.PresetId);
        Assert.Equal("ok", s1.Pieces[0].Route);
        Assert.Equal("pending", s1.Pieces[2].Route);

        string op2 = await OpIdAsync(await c.PresetAsync("rock", s1.Version));
        var done2 = await c.WaitOperationAsync(op2, o => o.EngineConfirmed);
        Assert.Equal("preset", done2.Kind);
        Assert.Equal("rock", done2.PresetId);
        var s2 = await c.StateAsync();
        Assert.Equal("rock", s2.Kit.PresetId);
        Assert.Equal("Rock 1", s2.Kit.Name);
        Assert.Equal("kick-a.wav", s2.Pieces[0].Sample!.Id);
        Assert.Equal(-2, s2.Pieces[0].OutputGainDb);
        Assert.Null(s2.Pieces[2].Sample);
        Assert.True(h.AudioAlive);
    }

    [Fact]
    public async Task AudioStopped_AppliedButNotEngineConfirmed()
    {
        await using var h = await PanelTestHost.StartAsync(audio: false);
        using var c = await h.PairedClientAsync();
        string op = await OpIdAsync(await c.SetSampleAsync("kick", "kick-b.wav", 0));
        var done = await c.WaitOperationAsync(op, o => o.Status == "applied");
        Assert.False(done.EngineConfirmed);
        Assert.False(done.AudioRunning);
        // Audio starts later: the engine takes the pending kit and the operation becomes confirmed.
        h.StartAudio();
        await c.WaitOperationAsync(op, o => o.EngineConfirmed);
    }

    [Fact]
    public async Task FailedWav_KeepsTheKit_WithPerPieceErrorCodes()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        using var c = await h.PairedClientAsync();
        var s0 = await c.StateAsync();
        await c.WaitOperationAsync(await OpIdAsync(await c.PresetAsync("leve", s0.Version)), o => o.EngineConfirmed);
        var kitBefore = h.Engine.ActiveKit;
        var stateBefore = await c.StateAsync();

        var broken = await c.WaitOperationAsync(await OpIdAsync(await c.PresetAsync("quebrado", stateBefore.Version)),
            o => o.Status is "failed" or "applied");
        Assert.Equal("failed", broken.Status);
        var err = Assert.Single(broken.Errors);
        Assert.Equal("kick", err.Piece);
        Assert.Equal("file_not_found", err.Code);
        Assert.False(string.IsNullOrWhiteSpace(err.Message));
        Assert.Same(kitBefore, h.Engine.ActiveKit);                                 // nothing changed

        var corrupt = await c.WaitOperationAsync(await OpIdAsync(await c.SetSampleAsync("tom", "corrompido.wav", stateBefore.Version)),
            o => o.Status is "failed" or "applied");
        Assert.Equal("failed", corrupt.Status);
        Assert.Equal("unsupported_format", Assert.Single(corrupt.Errors).Code);
        Assert.Same(kitBefore, h.Engine.ActiveKit);
        var after = await c.StateAsync();
        Assert.Equal(stateBefore.Version, after.Version);
        Assert.Equal(stateBefore.Pieces.Select(p => p.Sample?.Id), after.Pieces.Select(p => p.Sample?.Id));
    }

    [Theory]
    [InlineData("../fora.wav", 400, "invalid_sample_id")]
    [InlineData("C:/Windows/win.ini", 400, "invalid_sample_id")]
    [InlineData("\\\\server\\share\\a.wav", 400, "invalid_sample_id")]
    [InlineData("caixas/../../x.wav", 400, "invalid_sample_id")]
    [InlineData("NUL.wav", 400, "invalid_sample_id")]
    [InlineData("", 400, "invalid_sample_id")]
    [InlineData("nao-existe.wav", 404, "sample_not_found")]
    public async Task BadSampleIds_AreRefusedBeforeTheController(string sampleId, int status, string code)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var r = await c.SetSampleAsync("kick", sampleId, 0);
        Assert.Equal(status, (int)r.StatusCode);
        Assert.Equal(code, (await r.ProblemAsync()).Code);
        Assert.Equal(0, h.Controller.Version);
    }

    [Fact]
    public async Task OtherValidationErrors()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        Assert.Equal("unknown_piece", (await (await c.SetSampleAsync("cowbell", "kick-a.wav", 0)).ProblemAsync()).Code);
        Assert.Equal("unknown_piece", (await (await c.SetSampleAsync("Kick", "kick-a.wav", 0)).ProblemAsync()).Code);
        Assert.Equal("invalid_client_op_id", (await (await c.PostAsync("/api/pieces/kick/sample",
            new { sampleId = "kick-a.wav", clientOpId = "abc", baseVersion = 0 })).ProblemAsync()).Code);
        Assert.Equal("missing_base_version", (await (await c.PostAsync("/api/pieces/kick/sample",
            new { sampleId = "kick-a.wav", clientOpId = Guid.NewGuid() })).ProblemAsync()).Code);
        Assert.Equal("preset_not_found", (await (await c.PresetAsync("../rock", 0)).ProblemAsync()).Code);
        Assert.Equal("invalid_preset_id", (await (await c.PresetAsync("", 0)).ProblemAsync()).Code);
        Assert.Equal("operation_not_found", (await (await c.GetAsync($"/api/operations/{Guid.NewGuid()}")).ProblemAsync()).Code);
        Assert.Equal("operation_not_found", (await (await c.GetAsync("/api/operations/xyz")).ProblemAsync()).Code);
        Assert.Equal("not_found", (await (await c.GetAsync("/api/nada")).ProblemAsync()).Code);
        Assert.Equal("not_found", (await (await c.GetAsync("/assets/../secret")).ProblemAsync()).Code);
        Assert.Equal(0, h.Controller.Version);
    }

    [Fact]
    public async Task SameClientOpId_ReturnsTheSameOperation_OnlyOnce()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        string id = Guid.NewGuid().ToString();
        string op1 = await OpIdAsync(await c.SetSampleAsync("kick", "kick-a.wav", 0, id));
        await c.WaitOperationAsync(op1, o => o.Status == "applied");
        // Network dropped, the phone retries the same intention (baseVersion now old): same operation, no new change.
        string op2 = await OpIdAsync(await c.SetSampleAsync("kick", "kick-a.wav", 0, id));
        Assert.Equal(op1, op2);
        Assert.Equal(1, h.Controller.Version);
        var op = await c.GetJsonAsync<OperationDto>($"/api/operations/{op1}");
        Assert.Equal(id, op.ClientOpId);
        // Same id, other content: refused.
        var reused = await c.SetSampleAsync("kick", "kick-b.wav", 1, id);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("client_op_reused", (await reused.ProblemAsync()).Code);
        // Another device with the same clientOpId is a different operation.
        using var other = await h.PairedClientAsync("outro");
        string op3 = await OpIdAsync(await other.SetSampleAsync("snare", "caixas/caixa-1.wav", 1, id));
        Assert.NotEqual(op1, op3);
    }

    [Fact]
    public async Task StaleBaseVersion_409_WithCurrentState_OnlyForTheTargetedPiece()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var phone = await h.PairedClientAsync();
        var seen = await phone.StateAsync();                                         // version 0

        // Meanwhile the PC (or another phone) changes the snare.
        await h.Controller.SetPieceSample(DrumPiece.Snare, "caixas/caixa-1.wav").Completion;
        var stale = await phone.SetSampleAsync("snare", "caixas/caixa-2.wav", seen.Version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.ProblemAsync();
        Assert.Equal("stale_state", problem.Code);
        Assert.NotNull(problem.State);
        Assert.Equal(1, problem.State!.Version);
        Assert.Equal("caixas/caixa-1.wav", problem.State.Pieces[1].Sample!.Id);
        Assert.Equal("caixa-1", h.Engine.ActiveKit![DrumPiece.Snare].Name);         // newer selection kept

        // Another piece is not affected by the snare change: accepted with the old baseVersion.
        await phone.WaitOperationAsync(await OpIdAsync(await phone.SetSampleAsync("kick", "kick-a.wav", seen.Version)),
            o => o.Status == "applied");
        // A PC gain change does not make a selection stale.
        await h.Controller.SetPieceGain(DrumPiece.Snare, -4).Completion;
        var fresh = await phone.StateAsync();
        await h.Controller.SetPieceGain(DrumPiece.Tom, -1).Completion;
        Assert.Equal(HttpStatusCode.Accepted, (await phone.SetSampleAsync("snare", "caixas/caixa-2.wav", fresh.Version)).StatusCode);

        // A preset needs the whole kit unchanged since baseVersion.
        Assert.Equal("stale_state", (await (await phone.PresetAsync("rock", seen.Version)).ProblemAsync()).Code);
        // baseVersion from the future (app restarted): stale too.
        Assert.Equal("stale_state", (await (await phone.PresetAsync("rock", 9999)).ProblemAsync()).Code);
        Assert.Equal("stale_state", (await (await phone.SetSampleAsync("tom", "kick-a.wav", 9999)).ProblemAsync()).Code);
    }

    [Fact]
    public async Task ConcurrentPhones_SameBaseVersion_OnlyOneWins_TheOtherGets409()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        using var a = await h.PairedClientAsync("A");
        using var b = await h.PairedClientAsync("B");
        var s = await a.StateAsync();
        string opA = await OpIdAsync(await a.SetSampleAsync("snare", "caixas/caixa-1.wav", s.Version));
        await a.WaitOperationAsync(opA, o => o.EngineConfirmed);
        var rb = await b.SetSampleAsync("snare", "caixas/caixa-2.wav", s.Version);   // B saw the same old state
        Assert.Equal(HttpStatusCode.Conflict, rb.StatusCode);
        Assert.Equal("caixa-1", h.Engine.ActiveKit![DrumPiece.Snare].Name);
    }

    [Fact]
    public async Task PhoneTest_OffByDefault_ThenGatedByAudioAndSample()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var off = await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("test_not_allowed", (await off.ProblemAsync()).Code);
        Assert.False((await c.StateAsync()).AllowPhoneTest);

        h.Server.AppStatus = h.Server.AppStatus with { AllowPhoneTest = true };
        Assert.Equal("audio_stopped", (await (await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() })).ProblemAsync()).Code);
        h.StartAudio();
        Assert.Equal("no_sample", (await (await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() })).ProblemAsync()).Code);
        await h.Controller.SetPieceSample(DrumPiece.Kick, "kick-a.wav").Completion;
        string id = Guid.NewGuid().ToString();
        string op = await OpIdAsync(await c.PostAsync("/api/pieces/kick/test", new { clientOpId = id }));
        Assert.Equal(op, await OpIdAsync(await c.PostAsync("/api/pieces/kick/test", new { clientOpId = id })));   // idempotent
        Assert.Equal([DrumPiece.Kick], h.Tests);
        var dto = await c.GetJsonAsync<OperationDto>($"/api/operations/{op}");
        Assert.Equal("test", dto.Kind);
        Assert.Equal("applied", dto.Status);
    }

    [Fact]
    public async Task Commands_AreRateLimitedPerDevice()
    {
        var time = new ManualTime();                                              // frozen: one 1-second window
        await using var h = await PanelTestHost.StartAsync(audio: true, time: time);
        h.Server.AppStatus = h.Server.AppStatus with { AllowPhoneTest = true };
        await h.Controller.SetPieceSample(DrumPiece.Kick, "kick-a.wav").Completion;
        using var c = await h.PairedClientAsync();
        for (int i = 0; i < CommandLimiter.Limit; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() })).StatusCode);
        var limited = await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.ProblemAsync()).Code);
        Assert.NotNull(limited.Headers.RetryAfter);
        using var other = await h.PairedClientAsync("outro");                     // per device
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() })).StatusCode);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task PcOperations_CanBeQueried_WithoutClientOpId()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var pc = h.Controller.SetPieceGain(DrumPiece.Kick, -3);
        await pc.Completion;
        var dto = await c.GetJsonAsync<OperationDto>($"/api/operations/{pc.Id}");
        Assert.Null(dto.ClientOpId);
        Assert.Equal("pieceGain", dto.Kind);
        Assert.Equal("applied", dto.Status);
    }

    [Fact]
    public async Task PendingOperation_IsShownInTheState()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var gate = new ManualResetEventSlim(false);
        h.Controller.BeforeApply = _ => gate.Wait(TimeSpan.FromSeconds(10));
        try
        {
            string op = await OpIdAsync(await c.SetSampleAsync("tom", "kick-a.wav", 0));
            var s = await c.StateAsync();
            Assert.Equal(op, s.Pieces.Single(p => p.Piece == "tom").PendingOpId);
            Assert.Null(s.Pieces.Single(p => p.Piece == "kick").PendingOpId);
        }
        finally { gate.Set(); }
    }
}
