using SampleDrum.Core;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>
/// Onset detection behaviour. Timing assertions use a tolerance of ±1 ms (48 samples @ 48 kHz) between the
/// synthetic hit start and the reported onset (threshold crossing).
/// </summary>
public class DetectorTests
{
    private static readonly TriggerSettings Defaults = TriggerSettings.Default;

    [Fact]
    public void Silence_ProducesNoEvents_AndExactlySilentOutput()
    {
        var mic = new float[Fs * 2];
        var r = Run(mic, Defaults);
        Assert.Empty(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void NoiseFloor_ProducesNoEvents_AndExactlySilentOutput()
    {
        var r = Run(NoiseFloor(3), Defaults);
        Assert.Empty(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(-3f, 600f)]
    [InlineData(-6f, 1200f)]
    [InlineData(-12f, 900f)]
    public void SingleHitWithLongSustain_TriggersExactlyOnce(float levelDb, float sustainMs)
    {
        var mic = NoiseFloor(5);
        long start = Ms(300);
        AddKick(mic, start, levelDb, sustainMs);
        var r = Run(mic, Defaults);
        var e = Assert.Single(r.Events);
        Assert.InRange(e.SampleIndex, start, start + ToleranceSamples);
    }

    /// <summary>Settings with fast envelope/rearm so that close hits are limited only by the lockout.</summary>
    private static TriggerSettings FastRearm(float minIntervalMs) => Defaults with
    {
        EnvelopeReleaseMs = 3f,
        RearmTimeMs = 3f,
        MinIntervalMs = minIntervalMs,
    };

    [Theory]
    [InlineData(40.0, 30f)]
    [InlineData(60.0, 50f)]
    [InlineData(100.0, 50f)]
    [InlineData(100.0, 90f)]
    public void TwoCloseHits_SpacingAboveMinInterval_BothDetected(double spacingMs, float minIntervalMs)
    {
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(spacingMs);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 4f, seed: 2);

        var r = Run(mic, FastRearm(minIntervalMs));

        Assert.Equal(2, r.Events.Count);
        Assert.InRange(r.Events[0].SampleIndex, a, a + ToleranceSamples);
        Assert.InRange(r.Events[1].SampleIndex, b, b + ToleranceSamples);
    }

    [Theory]
    [InlineData(40.0, 50f)]
    [InlineData(60.0, 80f)]
    [InlineData(100.0, 120f)]
    public void TwoCloseHits_SpacingBelowMinInterval_SecondSuppressed(double spacingMs, float minIntervalMs)
    {
        var mic = NoiseFloor(1.5);
        long a = Ms(300), b = a + Ms(spacingMs);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 4f, seed: 2);

        var r = Run(mic, FastRearm(minIntervalMs));

        var e = Assert.Single(r.Events);
        Assert.InRange(e.SampleIndex, a, a + ToleranceSamples);
    }

    [Fact]
    public void SuppressedHit_DoesNotRetriggerLaterFromItsSustain()
    {
        // The second hit falls inside the lockout and has a long sustain that is still above the threshold
        // when the lockout ends: it must not fire late.
        var mic = NoiseFloor(2);
        long a = Ms(300), b = a + Ms(30);
        AddKick(mic, a, -6f, 4f, seed: 1);
        AddKick(mic, b, -6f, 400f, seed: 2);
        var r = Run(mic, FastRearm(50f));
        Assert.Single(r.Events);
    }

    public static IEnumerable<object[]> BlockSizes() =>
        new[] { 1, 7, 32, 61, 64, 127, 128, 256, 480, 512, 1000 }.Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void HitAcrossBlockBoundary_DetectedOnce_AtRightSample_SameAsWholeBuffer(int block)
    {
        // The hit starts 3 samples before a boundary common to 32/64/128/256/480 sample blocks,
        // so the crossing and the scan window straddle two blocks.
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
    [InlineData(128)]
    [InlineData(480)]
    [InlineData(61)]
    public void SyntheticTrack_AllHitsDetected_IdenticalForAnyBlockSize(int block)
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

    [Fact]
    public void Velocity_IsMonotonicWithHitLevel()
    {
        float[] levels = { -29f, -26f, -22f, -18f, -14f, -10f, -8f, -6f };
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
        Assert.InRange(vel[0], 0f, 0.1f);
        Assert.InRange(vel[^1], 0.95f, 1f);
    }

    [Fact]
    public void Velocity_DynamicsZero_AllHitsFullGain()
    {
        var s = Defaults with { Dynamics = 0f };
        foreach (var level in new[] { -25f, -15f, -5f })
        {
            var mic = NoiseFloor(1);
            AddKick(mic, Ms(200), level, 60f);
            var e = Assert.Single(Run(mic, s).Events);
            Assert.Equal(1f, e.Gain, 5);
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(61)]
    public void ScanZero_VoiceStartsAtCrossingSample_InSameBlock(int block)
    {
        var s = Defaults with { ScanMs = 0f, Dynamics = 0f };
        var mic = new float[Fs];
        long hit = 1000 + block / 2;           // somewhere inside a block
        mic[hit] = 0.5f;                       // single-sample impulse above the -30 dBFS threshold
        var r = Run(mic, s, block, DcSample(100));

        var e = Assert.Single(r.Events);
        Assert.Equal(hit, e.SampleIndex);
        Assert.Equal(hit, e.PlaybackSampleIndex);
        Assert.Equal((int)hit, FirstNonZero(r.Output));
        Assert.Equal(1f, r.Output[hit]);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(5f)]
    public void ScanWindow_VoiceStartsAtCrossingPlusScan(float scanMs)
    {
        var s = Defaults with { ScanMs = scanMs };
        var mic = new float[Fs];
        long hit = 4000;
        mic[hit] = 0.5f;
        var r = Run(mic, s, 128, DcSample(100));
        var e = Assert.Single(r.Events);
        long scan = Ms(scanMs);
        Assert.Equal(hit, e.SampleIndex);
        Assert.Equal(hit + scan, e.PlaybackSampleIndex);
        Assert.Equal((int)(hit + scan), FirstNonZero(r.Output));
    }

    [Fact]
    public void ScanWindow_CapturesPeakAfterCrossing()
    {
        // Crossing at -28 dBFS, real peak (-6 dBFS) 1 ms later: the scan window must report the peak.
        var mic = new float[Fs];
        mic[1000] = Db.ToLinear(-28f);
        mic[1048] = Db.ToLinear(-6f);
        var withScan = Assert.Single(Run(mic, Defaults with { ScanMs = 2f }).Events);
        var noScan = Assert.Single(Run(mic, Defaults with { ScanMs = 0f }).Events);
        Assert.Equal(-6f, Db.FromLinear(withScan.Peak), 1);
        Assert.Equal(-28f, Db.FromLinear(noScan.Peak), 1);
        Assert.True(withScan.Velocity > noScan.Velocity);
    }

    [Fact]
    public void Threshold_HitBelowThreshold_NotDetected()
    {
        var mic = NoiseFloor(1);
        AddKick(mic, Ms(200), -35f, 60f);
        Assert.Empty(Run(mic, Defaults).Events);
    }

    [Fact]
    public void Rearm_RequiresEnvelopeBelowRearmLevelForRearmTime()
    {
        // Two impulses 120 ms apart, lockout 10 ms. Between them the input stays at -35 dBFS
        // (below the -30 threshold, above the -40 rearm level): the detector stays disarmed.
        var s = Defaults with { MinIntervalMs = 10f, ScanMs = 0f };
        var mic = new float[Fs];
        int a = 1000, b = a + (int)Ms(120);
        mic[a] = 0.5f;
        for (int i = a + 1; i < b; i++) mic[i] = Db.ToLinear(-35f) * (i % 2 == 0 ? 1 : -1);
        mic[b] = 0.5f;
        Assert.Single(Run(mic, s).Events);

        // Same, but the gap is silent: the detector re-arms after RearmTimeMs and catches the second impulse.
        var mic2 = new float[Fs];
        mic2[a] = 0.5f;
        mic2[b] = 0.5f;
        Assert.Equal(2, Run(mic2, s).Events.Count);
    }

    [Fact]
    public void HighPass_RemovesSubsonicRumbleFromDetection()
    {
        // A 5 Hz rumble at -20 dBFS would trigger the -30 dBFS threshold; a 40 Hz high-pass on the detector
        // path removes it (> 40 dB attenuation at 5 Hz for a 2nd-order filter).
        var mic = new float[Fs * 2];
        for (int i = 0; i < mic.Length; i++) mic[i] = Db.ToLinear(-20f) * MathF.Sin(2 * MathF.PI * 5 * i / Fs);
        Assert.NotEmpty(Run(mic, Defaults).Events);
        Assert.Empty(Run(mic, Defaults with { HighPassHz = 40f }).Events);
    }
}
