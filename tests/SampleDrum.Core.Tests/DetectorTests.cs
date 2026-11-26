using SampleDrum.Core;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>
/// Onset detection behaviour. Numbers (#n) refer to "O que vamos testar" in docs/research/lsp-trigger.md.
/// Timing assertions use ±1 ms (48 samples @ 48 kHz) between the synthetic hit start and the reported onset
/// (threshold crossing), unless stated otherwise.
/// </summary>
public class DetectorTests
{
    private static readonly TriggerSettings Defaults = TriggerSettings.Default;

    // ---------- #1 silence ----------

    [Fact]
    public void Silence_ProducesNoEvents_AndExactlySilentOutput()
    {
        var r = Run(new float[Fs * 2], Defaults);
        Assert.Empty(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(-70f)]
    [InlineData(-60f)]
    public void NoiseFloor_NoEvents_SilentOutput_EnvelopeDoesNotDrift(float rmsDb)
    {
        var mic = NoiseFloor(5, rmsDb: rmsDb);
        var r = Run(mic, Defaults);
        Assert.Empty(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));

        var d = new OnsetDetector(Fs, Defaults);
        float maxEnvLastSecond = 0f;
        for (int i = 0; i < mic.Length; i++)
        {
            d.Step(mic[i], i, out _);
            if (i >= mic.Length - Fs) maxEnvLastSecond = Math.Max(maxEnvLastSecond, d.Envelope);
        }
        Assert.True(Db.FromLinear(maxEnvLastSecond) < rmsDb + 12f, $"envelope drifted to {Db.FromLinear(maxEnvLastSecond)} dB");
    }

    // ---------- #2 single hit with long sustain ----------

    [Theory]
    [InlineData(-3f, 600f, 15f)]
    [InlineData(-6f, 1200f, 15f)]
    [InlineData(-12f, 900f, 15f)]
    [InlineData(-3f, 400f, 0f)]    // minimal release time: zero crossings must not re-arm
    [InlineData(-6f, 1200f, 0f)]
    public void SingleHitWithLongSustain_TriggersExactlyOnce(float levelDb, float sustainMs, float releaseTimeMs)
    {
        var mic = NoiseFloor(5);
        long start = Ms(300);
        AddKick(mic, start, levelDb, sustainMs);
        var r = Run(mic, Defaults with { RearmTimeMs = releaseTimeMs });
        var e = Assert.Single(r.Events);
        Assert.InRange(e.SampleIndex, start, start + ToleranceSamples);
    }

    [Theory]
    [InlineData(15f)]
    [InlineData(0f)]
    public void Pure55HzSustain_400ms_WithClick_TriggersExactlyOnce(float releaseTimeMs)
    {
        // Research test #2 literally: 55 Hz sine decaying over ~400 ms with an attack click.
        var mic = NoiseFloor(4);
        long start = Ms(200);
        AddKick(mic, start, -3f, 400f, startHz: 55f, endHz: 55f);
        var r = Run(mic, Defaults with { RearmTimeMs = releaseTimeMs });
        var e = Assert.Single(r.Events);
        Assert.InRange(e.SampleIndex, start, start + ToleranceSamples);
    }

    // ---------- #3 two close hits ----------

    [Fact]
    public void TwoCloseHits_SpacingBelowMinInterval_OneEvent()
    {
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(20);                  // 20 ms < default 35 ms lockout
        AddKick(mic, a, -6f, 40f, seed: 1);
        AddKick(mic, b, -6f, 40f, seed: 2);
        var e = Assert.Single(Run(mic, Defaults).Events);
        Assert.InRange(e.SampleIndex, a, a + ToleranceSamples);
    }

    [Theory]
    [InlineData(40.0, 25f)]
    [InlineData(60.0, 40f)]
    [InlineData(100.0, 70f)]
    public void TwoCloseHits_SustainAboveRelease_BothDetectedByRetrigger(double spacingMs, float sustainMs)
    {
        // Equal-level hits; the first one's sustain is still above the release level when the second lands
        // (asserted below), so only the rise re-trigger can catch the second hit. Each sustain has decayed
        // ~12 dB by then; the re-trigger needs the new hit to rise RetriggerRiseDb (9 dB) above the decayed
        // sustain, so equal hits on a sustain that decayed only a few dB are still merged (documented limit).
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(spacingMs);
        AddKick(mic, a, -6f, sustainMs, seed: 1);
        AddKick(mic, b, -6f, sustainMs, seed: 2);

        Assert.NotEqual(DetectorState.Off, StateBefore(mic, b, Defaults));

        var r = Run(mic, Defaults);
        Assert.Equal(2, r.Events.Count);
        Assert.InRange(r.Events[0].SampleIndex, a, a + ToleranceSamples);
        Assert.InRange(r.Events[1].SampleIndex, b, b + ToleranceSamples);
        Assert.True(r.Events[1].IsRetrigger);

        // Without the re-trigger the second hit is lost (LSP-like behaviour).
        Assert.Single(Run(mic, Defaults with { RetriggerRiseDb = 0f }).Events);
    }

    /// <summary>Settings with fast envelope/release so that close hits are limited only by the lockout.</summary>
    private static TriggerSettings FastRearm(float minIntervalMs) => Defaults with
    {
        EnvelopeReleaseMs = 3f,
        RearmTimeMs = 3f,
        MinIntervalMs = minIntervalMs,
        RetriggerRiseDb = 0f,
    };

    [Theory]
    [InlineData(40.0, 30f)]
    [InlineData(60.0, 50f)]
    [InlineData(100.0, 90f)]
    public void TwoShortHits_SpacingAboveMinInterval_BothDetected(double spacingMs, float minIntervalMs)
    {
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(spacingMs);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 4f, seed: 2);
        var r = Run(mic, FastRearm(minIntervalMs));
        Assert.Equal(2, r.Events.Count);
        Assert.InRange(r.Events[1].SampleIndex, b, b + ToleranceSamples);
    }

    [Theory]
    [InlineData(40.0, 50f)]
    [InlineData(60.0, 80f)]
    [InlineData(100.0, 120f)]
    public void TwoShortHits_SpacingBelowMinInterval_SecondSuppressed(double spacingMs, float minIntervalMs)
    {
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(spacingMs);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 4f, seed: 2);
        foreach (var s in new[] { FastRearm(minIntervalMs), FastRearm(minIntervalMs) with { RetriggerRiseDb = 9f } })
        {
            var e = Assert.Single(Run(mic, s).Events);
            Assert.InRange(e.SampleIndex, a, a + ToleranceSamples);
        }
    }

    [Fact]
    public void SuppressedHit_DoesNotFireLaterFromItsSustain()
    {
        // The second hit falls inside the lockout and has a long sustain still above the threshold
        // when the lockout ends: it must not fire late (neither by re-arm nor by re-trigger).
        var mic = NoiseFloor(2);
        long a = Ms(300), b = a + Ms(30);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 400f, seed: 2);
        Assert.Single(Run(mic, Defaults with { MinIntervalMs = 50f }).Events);
        Assert.Single(Run(mic, FastRearm(50f)).Events);
    }

    [Fact]
    public void Retrigger_WithTooShortEnvelopeRelease_FiresOnRipple_DocumentedHazard()
    {
        // Documents why EnvelopeReleaseMs must exceed half a period of the kick fundamental when the rise
        // re-trigger is on: a 3 ms release lets the envelope collapse between the peaks of a 50 Hz sustain,
        // so every cycle looks like a new rise. The default 20 ms release is safe (see the 55 Hz tests).
        var mic = NoiseFloor(2);
        AddKick(mic, Ms(300), -6f, 400f, startHz: 50f, endHz: 50f);
        Assert.True(Run(mic, Defaults with { EnvelopeReleaseMs = 3f }).Events.Count > 1);
        Assert.Single(Run(mic, Defaults).Events);
    }

    // ---------- #4 block boundaries ----------

    public static IEnumerable<object[]> BlockSizes() =>
        new[] { 1, 7, 32, 61, 64, 127, 128, 256, 480, 512, 1000, 1024 }.Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void HitAcrossBlockBoundary_DetectedOnce_AtRightSample_SameAsWholeBuffer(int block)
    {
        // The hit starts 3 samples before a boundary common to 32/64/128/256/480/512/1024 sample blocks,
        // so the crossing and the detect window straddle two blocks.
        long start = SyntheticSignals.CommonBlockBoundary - 3;
        var mic = NoiseFloor(6);
        AddKick(mic, start, -8f, 60f);

        var whole = Run(mic, Defaults, block: mic.Length);
        var blocked = Run(mic, Defaults, block: block);

        var e = Assert.Single(blocked.Events);
        Assert.InRange(e.SampleIndex, start, start + ToleranceSamples);
        Assert.Equal(whole.Events, blocked.Events);
        Assert.Equal(whole.Output, blocked.Output);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(480)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(61)]
    public void SyntheticTrack_AllHitsDetected_IdenticalEventsAndOutputForAnyBlockSize(int block)
    {
        var (mic, hits) = SyntheticSignals.MicTrack(Fs);
        var r = Run(mic, Defaults, block);
        var whole = Run(mic, Defaults, mic.Length);

        var expected = hits.Select(h => h.StartSample).OrderBy(x => x).ToList();
        var detected = r.Events.Select(e => e.SampleIndex).ToList();
        var matches = OnsetMatcher.Match(expected, detected, ToleranceSamples);

        Assert.All(matches, m => Assert.True(m.IsMatch, $"miss/extra: {m}"));
        Assert.All(matches, m => Assert.InRange(m.ErrorSamples, 0, ToleranceSamples));
        Assert.Equal(whole.Events, r.Events);
        Assert.Equal(whole.Output, r.Output);
    }

    // ---------- #5 velocity ----------

    [Fact]
    public void Velocity_ResearchLadder_IncreasingAndGainWithinRange()
    {
        var s = Defaults.WithThreshold(-36f);
        var vel = new List<float>();
        foreach (var level in new[] { -30f, -18f, -6f })
        {
            var mic = NoiseFloor(1);
            AddKick(mic, Ms(200), level, 60f);
            var e = Assert.Single(Run(mic, s).Events);
            vel.Add(e.Velocity);
            Assert.InRange(Db.FromLinear(e.Gain), s.MinGainDb - 1e-3f, 1e-3f);
        }
        Assert.True(vel[0] < vel[1] && vel[1] < vel[2], string.Join(", ", vel));
    }

    [Fact]
    public void Velocity_IsMonotonicWithHitLevel_FineSteps()
    {
        float[] levels = { -22f, -19f, -16f, -13f, -10f, -8f, -6f, -3f };
        var vel = new List<float>();
        var gain = new List<float>();
        foreach (var level in levels)
        {
            var mic = NoiseFloor(1);
            AddKick(mic, Ms(200), level, 60f);
            var e = Assert.Single(Run(mic, Defaults).Events);
            vel.Add(e.Velocity);
            gain.Add(e.Gain);
        }
        for (int i = 1; i < levels.Length; i++)
        {
            Assert.True(vel[i] > vel[i - 1], $"velocity not increasing at {levels[i]} dB: {vel[i - 1]} -> {vel[i]}");
            Assert.True(gain[i] > gain[i - 1], $"gain not increasing at {levels[i]} dB");
        }
    }

    [Fact]
    public void Velocity_SaturatesAboveThresholdPlusRange()
    {
        // Burst starting at full amplitude: the window peak is the true peak (-3 dBFS), 33 dB above -36.
        var s = Defaults.WithThreshold(-36f);
        var mic = new float[Fs];
        AddCosBurst(mic, 4800, -3f, 200, 20);
        var e = Assert.Single(Run(mic, s).Events);
        Assert.Equal(1f, e.Velocity);
        Assert.Equal(1f, e.Gain, 5);
    }

    [Fact]
    public void FixedVelocity_AllHitsFullGain()
    {
        var s = Defaults with { FixedVelocity = true };
        foreach (var level in new[] { -18f, -10f, -3f })
        {
            var mic = NoiseFloor(1);
            AddKick(mic, Ms(200), level, 60f);
            var e = Assert.Single(Run(mic, s).Events);
            Assert.Equal(1f, e.Gain, 5);
            Assert.True(e.Velocity < 1f);  // still measured and reported
        }
    }

    // ---------- #6 low-frequency ringing and bleed ----------

    [Fact]
    public void LowFrequencyRingingBelowThreshold_NoTrigger()
    {
        var mic = NoiseFloor(3);
        for (int i = 0; i < mic.Length; i++) mic[i] += Db.ToLinear(-30f) * MathF.Sin(2 * MathF.PI * 50 * i / Fs);
        Assert.Empty(Run(mic, Defaults).Events);
    }

    [Fact]
    public void SnareBleed20dBBelowKick_NoFalseTriggers_KicksStillDetected()
    {
        var mic = NoiseFloor(4);
        long[] kicks = { Ms(250), Ms(1250), Ms(2250), Ms(3250) };
        foreach (var k in kicks) AddKick(mic, k, -6f, 150f);
        foreach (var k in kicks) AddSnare(mic, k + Ms(500), -26f);   // backbeat between kicks, -20 dB re kick
        var r = Run(mic, Defaults);
        Assert.Equal(kicks.Length, r.Events.Count);
        for (int i = 0; i < kicks.Length; i++) Assert.InRange(r.Events[i].SampleIndex, kicks[i], kicks[i] + ToleranceSamples);
    }

    [Fact]
    public void LoudBleedAboveThreshold_RejectedByLowPass_KicksStillDetected()
    {
        var mic = NoiseFloor(4);
        long[] kicks = { Ms(250), Ms(1250), Ms(2250), Ms(3250) };
        foreach (var k in kicks) AddKick(mic, k, -6f, 150f);
        foreach (var k in kicks) AddSnare(mic, k + Ms(500), -14f);   // bleed 10 dB above the threshold

        Assert.True(Run(mic, Defaults).Events.Count > kicks.Length, "bleed should trigger without the low-pass");

        var r = Run(mic, Defaults with { LowPassHz = 150f });
        Assert.Equal(kicks.Length, r.Events.Count);
        // The low-pass adds group delay to the detector path: tolerance 2 ms here.
        for (int i = 0; i < kicks.Length; i++) Assert.InRange(r.Events[i].SampleIndex, kicks[i], kicks[i] + 2 * ToleranceSamples);
    }

    // ---------- #7 isolated click ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsolatedClick_RejectedByDetectTime(int clickSamples)
    {
        var mic = NoiseFloor(1);
        for (int i = 0; i < clickSamples; i++) mic[Ms(300) + i] += Db.ToLinear(-18f);   // 6 dB above threshold
        Assert.Empty(Run(mic, Defaults).Events);
        Assert.Single(Run(mic, Defaults with { DetectTimeMs = 0f }).Events);
    }

    [Fact]
    public void IsolatedClick_ThenRealHit_HitStillDetectedAtItsOnset()
    {
        // The aborted click must not touch the lockout nor block the real hit 10 ms later.
        var mic = NoiseFloor(1);
        mic[Ms(300)] += Db.ToLinear(-12f);
        long hit = Ms(310);
        AddKick(mic, hit, -6f, 60f);
        var e = Assert.Single(Run(mic, Defaults).Events);
        Assert.InRange(e.SampleIndex, hit, hit + ToleranceSamples);
    }

    // ---------- #8 latency ----------

    [Theory]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(61)]
    public void Latency_SampleStartsWithinTriggerWindowPlusOneSample(int block)
    {
        var mic = new float[Fs];
        long onset = 4000 + block / 3;
        AddCosBurst(mic, onset, -6f, 100, 30);
        var r = Run(mic, Defaults, block, DcSample(1000));
        var e = Assert.Single(r.Events);
        // Bound = the configured trigger window, max(DetectTimeMs, ScanMs) (2 ms by default), + 1 sample.
        long window = Math.Max(Ms(Defaults.DetectTimeMs), Ms(Defaults.ScanMs));
        Assert.Equal(onset, e.SampleIndex);
        Assert.InRange(e.PlaybackSampleIndex - onset, 0, window + 1);
        Assert.Equal((int)e.PlaybackSampleIndex, FirstNonZero(r.Output));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(61)]
    public void NoDebounceNoScan_VoiceStartsAtCrossingSample_InSameBlock(int block)
    {
        var mic = new float[Fs];
        long hit = 1000 + block / 2;           // somewhere inside a block
        mic[hit] = 0.5f;
        var r = Run(mic, Impulsive, block, DcSample(100));
        var e = Assert.Single(r.Events);
        Assert.Equal(hit, e.SampleIndex);
        Assert.Equal(hit, e.PlaybackSampleIndex);
        Assert.Equal((int)hit, FirstNonZero(r.Output));
        Assert.Equal(1f, r.Output[hit]);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.5f, 2f)]
    [InlineData(2f, 0.5f)]
    public void Window_VoiceStartsAtCrossingPlusMaxOfDetectAndScan(float scanMs, float detectMs)
    {
        var s = Defaults with { ScanMs = scanMs, DetectTimeMs = detectMs, HighPassHz = 0f };
        var mic = new float[Fs];
        long hit = 4000;
        AddCosBurst(mic, hit, -6f, 100, 30);
        var r = Run(mic, s, 128, DcSample(100));
        var e = Assert.Single(r.Events);
        long window = Math.Max(Ms(scanMs), Ms(detectMs));
        Assert.Equal(hit, e.SampleIndex);
        Assert.Equal(hit + window, e.PlaybackSampleIndex);
        Assert.Equal((int)(hit + window), FirstNonZero(r.Output));
    }

    [Fact]
    public void ScanWindow_CapturesPeakAfterCrossing()
    {
        // Crossing at -22 dBFS, real peak (-6 dBFS) 1 ms later: a 2 ms scan window reports the peak.
        var mic = new float[Fs];
        mic[1000] = Db.ToLinear(-22f);
        mic[1048] = Db.ToLinear(-6f);
        var noDebounce = Impulsive with { FixedVelocity = false };
        var withScan = Assert.Single(Run(mic, noDebounce with { ScanMs = 2f }).Events);
        var noScan = Assert.Single(Run(mic, noDebounce with { ScanMs = 0f }).Events);
        Assert.Equal(-6f, Db.FromLinear(withScan.Peak), 1);
        Assert.Equal(-22f, Db.FromLinear(noScan.Peak), 1);
        Assert.True(withScan.Velocity > noScan.Velocity);
    }

    // ---------- thresholds, release, filters ----------

    [Fact]
    public void Threshold_HitBelowThreshold_NotDetected()
    {
        var mic = NoiseFloor(1);
        AddKick(mic, Ms(200), -28f, 60f);
        Assert.Empty(Run(mic, Defaults).Events);
    }

    [Fact]
    public void Release_RequiresEnvelopeBelowReleaseLevelForReleaseTime()
    {
        // Two impulses 120 ms apart. Between them the input stays at -27 dBFS (below the -24 threshold,
        // above the -30 release level): without re-trigger the detector stays ON and misses the second one.
        var s = Impulsive with { MinIntervalMs = 10f };
        int a = 1000, b = a + (int)Ms(120);
        var held = new float[Fs];
        held[a] = 0.5f;
        for (int i = a + 1; i < b; i++) held[i] = Db.ToLinear(-27f) * (i % 2 == 0 ? 1 : -1);
        held[b] = 0.5f;
        Assert.Single(Run(held, s).Events);

        // With the rise re-trigger (+9 dB over the -27 dB floor) the second impulse is accepted.
        var r = Run(held, s with { RetriggerRiseDb = 9f });
        Assert.Equal(2, r.Events.Count);
        Assert.True(r.Events[1].IsRetrigger);

        // Silent gap: the detector re-arms after the release time and catches the second impulse normally.
        var gap = new float[Fs];
        gap[a] = 0.5f;
        gap[b] = 0.5f;
        var g = Run(gap, s);
        Assert.Equal(2, g.Events.Count);
        Assert.False(g.Events[1].IsRetrigger);
    }

    [Fact]
    public void HighPass_RemovesSubsonicRumbleFromDetection()
    {
        // A 5 Hz rumble at -14 dBFS crosses the -24 dBFS threshold; the default 40 Hz high-pass removes it.
        var mic = new float[Fs * 2];
        for (int i = 0; i < mic.Length; i++) mic[i] = Db.ToLinear(-14f) * MathF.Sin(2 * MathF.PI * 5 * i / Fs);
        Assert.NotEmpty(Run(mic, Defaults with { HighPassHz = 0f }).Events);
        Assert.Empty(Run(mic, Defaults).Events);
    }

    [Fact]
    public void WithThreshold_KeepsReleaseOffset()
    {
        var s = TriggerSettings.Default.WithThreshold(-30f);
        Assert.Equal(-30f, s.ThresholdDb);
        Assert.Equal(-36f, s.RearmDb);
        Assert.Equal(-6f, s.ReleaseRelativeDb);
    }
}
