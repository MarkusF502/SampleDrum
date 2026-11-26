using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>Regression tests for the independent v2 review (probes on 75dc01e).</summary>
public sealed class ReviewV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-review2-" + Guid.NewGuid().ToString("N"));

    public ReviewV2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------- (a) half-configured pending piece must not block the runnable ones ----------

    [Fact]
    public void PendingPieceSharingKickInput_IsWarningOnly_KickStillRuns()
    {
        // Probe: Caixa in = USB 3 (same as Bumbo), out pending.
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(3, null));
        var v = RoutingValidator.Validate(prof);
        Assert.False(v.HasErrors);
        Assert.True(v.IsRunnable(DrumPiece.Kick));
        Assert.False(v.IsRunnable(DrumPiece.Snare));
        var dup = Assert.Single(v.Issues, i => i.Kind == RoutingIssueKind.DuplicateInput);
        Assert.Equal(DrumPiece.Snare, dup.Piece);
        Assert.True(dup.OnPendingPiece);
        Assert.False(dup.IsError);
        Assert.Equal(2, v.Resolve().InputIndex(DrumPiece.Kick));
    }

    [Fact]
    public void PendingPieceOnObsInput_IsWarningOnly_StartAllowed()
    {
        // Probe: Caixa in = USB 1 (reserved OBS), out pending.
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(1, null));
        var v = RoutingValidator.Validate(prof);
        Assert.False(v.HasErrors);
        Assert.Equal(new[] { DrumPiece.Kick }, v.RunnablePieces);
        var res = Assert.Single(v.Issues, i => i.Kind == RoutingIssueKind.Reserved);
        Assert.Equal(DrumPiece.Snare, res.Piece);
        Assert.False(res.IsError);
        Assert.Contains("OBS", res.Message);
    }

    [Fact]
    public void PendingPieceOutOfRange_IsWarningOnly()
    {
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Tom, new PieceRoute(40, null));
        var v = RoutingValidator.Validate(prof);
        Assert.False(v.HasErrors);
        Assert.True(Assert.Single(v.Issues, i => i.Kind == RoutingIssueKind.OutOfRange).OnPendingPiece);
    }

    [Fact]
    public void CompletePiecesSharingAChannel_StillAnError_PendingThirdOnlyWarned()
    {
        // Bumbo and Caixa both complete on input USB 3 (error); Tom pending on USB 3 too (warning on Tom only).
        var prof = RoutingProfiles.Church
            .WithRoute(DrumPiece.Snare, new PieceRoute(3, 2))
            .WithRoute(DrumPiece.Tom, new PieceRoute(3, null));
        var v = RoutingValidator.Validate(prof);
        Assert.True(v.HasErrors);
        var errors = v.Issues.Where(i => i.IsError).ToList();
        Assert.Equal(new[] { DrumPiece.Kick, DrumPiece.Snare }, errors.Select(i => i.Piece!.Value).OrderBy(p => p));
        Assert.Contains(v.Issues, i => i.Piece == DrumPiece.Tom && i.Kind == RoutingIssueKind.DuplicateInput && !i.IsError);
        Assert.Empty(v.RunnablePieces);
    }

    // ---------- (d) SettingsVersion not an int must not crash the app ----------

    [Theory]
    [InlineData("""{ "SettingsVersion": 2.5 }""")]
    [InlineData("""{ "SettingsVersion": 1e20 }""")]
    [InlineData("""{ "SettingsVersion": -1e400 }""")]
    [InlineData("""{ "SettingsVersion": "2" }""")]
    public void Load_InvalidSettingsVersion_DefaultsWithMessage(string json)
    {
        string path = Path.Combine(_dir, AppConfig.FileName);
        File.WriteAllText(path, json);
        var cfg = AppConfig.Load(path, out var msg);
        Assert.Contains("corrompido", msg);
        Assert.Equal(KitSettings.Default, cfg.ToKitSettings());
    }

    // ---------- (e) pending kit merge and detection overrides on current settings ----------

    [Fact]
    public void TwoApplyKitBeforeABlock_KeepTheFirstKitsGain()
    {
        // Probe: kit A sets kick -12 dB, kit B (no gain) replaces it before the audio thread takes A.
        var engine = new KitEngine(Fs, KitTestUtil.AllPieces(Impulsive)) { KitEnabled = false };
        engine.ApplyKit(KitTestUtil.DcKit(64).WithPiece(new PreparedPiece(DrumPiece.Kick, Enumerable.Repeat(1f, 64).ToArray(), null, "a")
        {
            GainDb = -12.0412f,                                          // x0.25
        }));
        engine.ApplyKit(KitTestUtil.DcKit(64));                        // no gains
        Assert.Equal(-12.0412f, engine.Settings[DrumPiece.Kick].OutputGainDb, 3);

        engine.RequestTest(DrumPiece.Kick);
        var outB = KitTestUtil.Mics(128.0 / Fs);
        engine.Process(KitTestUtil.Mics(128.0 / Fs), outB, 128);
        Assert.Equal(0.25f, outB[0][0], 3);                            // played at -12 dB, as Settings says
    }

    [Fact]
    public void PresetDetectionOverrides_GoOnTopOfCurrentCalibration_EngineAndConfigAgree()
    {
        // Calibrated snare: threshold -15, HPF 140 Hz. Preset only says MinIntervalMs = 40.
        var calibrated = PieceSettings.DefaultFor(DrumPiece.Snare) with
        {
            Trigger = KitDefaults.Snare.WithThreshold(-15f) with { HighPassHz = 140f },
        };
        var engine = new KitEngine(Fs, KitSettings.Default.With(DrumPiece.Snare, calibrated));
        var det = new TriggerOverrides { MinIntervalMs = 40f };
        engine.ApplyKit(KitTestUtil.DcKit(16).WithPiece(new PreparedPiece(DrumPiece.Snare, new[] { 1f }, null, "s") { Detection = det }));
        var t = engine.Settings[DrumPiece.Snare].Trigger;
        Assert.Equal(40f, t.MinIntervalMs);
        Assert.Equal(-15f, t.ThresholdDb);                              // calibration kept
        Assert.Equal(140f, t.HighPassHz);

        var cfg = AppConfig.CreateDefault();
        cfg.SetPieceSettings(DrumPiece.Snare, calibrated);
        cfg.ApplyPreset(new KitPreset
        {
            Pieces = new Dictionary<DrumPiece, PiecePreset> { [DrumPiece.Snare] = new(null, null, det) },
        });
        Assert.Equal(t, cfg.GetPieceSettings(DrumPiece.Snare).Trigger);  // config and engine identical
    }
}
