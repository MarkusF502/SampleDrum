using SampleDrum.Core;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>Regression tests for the independent review findings.</summary>
public class ReviewFixTests
{
    private static readonly TriggerSettings Defaults = TriggerSettings.Default;

    private static float ExpectedVelocity(float levelDb, TriggerSettings s)
        => Math.Clamp((levelDb - s.ThresholdDb) / s.VelocityRangeDb, 0f, 1f);

    private static TriggerEvent SingleHit(float[] hit, TriggerSettings s)
    {
        var mic = new float[Fs];
        SyntheticSignals.Add(mic, hit, 4800);
        return Assert.Single(Run(mic, s).Events);
    }

    // ---------- 1. velocity measured on the raw input over the scan window ----------

    public static IEnumerable<object[]> VelocityCases()
    {
        foreach (var lpf in new[] { 0f, 150f })
        foreach (var level in new[] { 0f, -6f, -12f })
        {
            yield return new object[] { "clicky", level, lpf };
            yield return new object[] { "clean-sweep", level, lpf };
        }
    }

    /// <summary>
    /// With the default 2 ms window, velocity follows the dB mapping of the hit's real level within ±0.05
    /// (±1.2 dB) for kicks with a beater click and for click-less kicks with a pitch sweep, with or without the
    /// low-pass. (Before the fix, a click-less kick at 0 dBFS read velocity ~0.43.)
    /// </summary>
    [Theory]
    [MemberData(nameof(VelocityCases))]
    public void Velocity_FollowsDbMapping_DefaultWindow(string kind, float levelDb, float lowPassHz)
    {
        var s = Defaults with { LowPassHz = lowPassHz };
        float peak = Db.ToLinear(levelDb);
        var hit = kind == "clicky"
            ? SyntheticSignals.Kick(Fs, peak, 150f, seed: 11)
            : SyntheticSignals.CleanKick(Fs, peak, 150f, startHz: 120f, endHz: 50f);
        var e = SingleHit(hit, s);
        Assert.InRange(e.Velocity, ExpectedVelocity(levelDb, s) - 0.05f, ExpectedVelocity(levelDb, s) + 0.05f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-6f, 0f)]
    [InlineData(-12f, 0f)]
    [InlineData(0f, 150f)]
    [InlineData(-6f, 150f)]
    public void Velocity_CleanLow55HzKick_WithinToleranceForWindow(float levelDb, float lowPassHz)
    {
        // Slowest attack possible (55 Hz sine from phase 0, peak at ~4.5 ms): 2 ms reads at most ~3.2 dB low,
        // 3 ms within ~1 dB (measured; see docs/README-dev.md).
        var hit = SyntheticSignals.CleanKick(Fs, Db.ToLinear(levelDb), 150f);
        var s2 = Defaults with { LowPassHz = lowPassHz };
        var s3 = s2 with { ScanMs = 3f };
        float exp = ExpectedVelocity(levelDb, s2);
        Assert.InRange(SingleHit(hit, s2).Velocity, exp - 0.15f, exp + 0.02f);
        Assert.InRange(SingleHit(hit, s3).Velocity, exp - 0.05f, exp + 0.02f);
    }

    [Fact]
    public void Velocity_ZeroWindowStillUnderestimates_DocumentedTradeOff()
    {
        var hit = SyntheticSignals.CleanKick(Fs, 1f, 150f);
        var e = SingleHit(hit, Defaults with { ScanMs = 0f });
        Assert.True(e.Velocity < 0.6f, $"velocity {e.Velocity}");
    }

    // ---------- 2. filter denormals ----------

    [Fact]
    public void FilterStates_ReachExactlyZero_InDigitalSilence()
    {
        var d = new OnsetDetector(Fs, Defaults with { LowPassHz = 200f });
        var hit = SyntheticSignals.Kick(Fs, 0.5f, 60f, seed: 3);
        long i = 0;
        foreach (var x in hit) d.Step(x, i++, out _);
        Assert.False(d.FilterStateIsZero);
        for (int k = 0; k < Fs * 10; k++) d.Step(0f, i++, out _);
        Assert.True(d.FilterStateIsZero);
        Assert.Equal(0f, d.Envelope);
    }

    // ---------- 3. engine reset between stop/start ----------

    [Fact]
    public void Reset_VoiceCutByStopDoesNotResume_AndPendingTestIsCleared()
    {
        var engine = new TriggerEngine(Fs, Impulsive);
        engine.SetSample(DcSample(Fs));
        var input = new float[128];
        input[10] = 0.5f;
        var o = new float[128];
        engine.Process(input, o);
        Assert.Equal(1f, o[127]);                       // voice playing when the "stop" happens

        engine.RequestTest();                            // a test click that never got processed
        engine.Reset();                                  // MainForm calls this before each start

        Array.Clear(input);
        Array.Clear(o);
        engine.Process(input, o);
        Assert.All(o, v => Assert.Equal(0f, v));
        Assert.Equal(0, engine.ActiveVoiceCount);
        while (engine.TryDequeueEvent(out var e)) Assert.False(e.IsManualTest);
    }

    [Fact]
    public void Reset_DetectorRearmed_NewHitDetectedImmediately()
    {
        var engine = new TriggerEngine(Fs, Impulsive);
        var input = new float[128];
        input[0] = 0.5f;
        engine.Process(input, new float[128]);           // detector now ON (disarmed) with a high envelope
        while (engine.TryDequeueEvent(out _)) { }
        engine.Reset();
        engine.Process(input, new float[128]);           // same hit right away: must fire after reset
        Assert.True(engine.TryDequeueEvent(out var e));
        Assert.Equal(128, e.SampleIndex);                // absolute counter keeps running
    }

    // ---------- 4. NaN / Inf ----------

    [Fact]
    public void FloatWav_WithNaNAndInf_ReadAsSilence_AndCounted()
    {
        using var ms = new MemoryStream();
        WavFile.WriteMono(ms, new[] { 0.5f, float.NaN, float.PositiveInfinity, -0.25f }, 48000, WavSampleFormat.Float32);
        ms.Position = 0;
        var w = WavFile.Read(ms);
        Assert.Equal(new[] { 0.5f, 0f, 0f, -0.25f }, w.Mono);
        Assert.Equal(2, w.NonFiniteSamples);
    }

    [Fact]
    public void ReadMonoAtRate_ReportsNonFiniteInNote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sd-nan-{Guid.NewGuid():N}.wav");
        try
        {
            WavFile.WriteMono(path, new[] { 0.1f, float.NaN, 0.2f }, 48000, WavSampleFormat.Float32);
            WavFile.ReadMonoAtRate(path, 48000, out var note);
            Assert.NotNull(note);
            Assert.Contains("1 amostra", note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetSample_SanitizesNonFinite_OutputStaysFinite()
    {
        var engine = new TriggerEngine(Fs, Impulsive) { Enabled = false };
        engine.SetSample(new[] { 0.5f, float.NaN, float.NegativeInfinity, 0.25f });
        Assert.Equal(2, engine.LastSampleNonFiniteCount);
        engine.RequestTest();
        var o = new float[8];
        engine.Process(new float[8], o);
        Assert.Equal(new[] { 0.5f, 0f, 0f, 0.25f, 0f, 0f, 0f, 0f }, o);
    }

    [Fact]
    public void Detector_NaNInput_DoesNotPoisonState()
    {
        var mic = new float[Fs];
        mic[1000] = float.NaN;
        mic[1001] = float.PositiveInfinity;
        long hit = 4800;
        AddCosBurst(mic, hit, -6f, 100, 30);
        var r = Run(mic, Defaults, 128, DcSample(100));
        var e = Assert.Single(r.Events);
        Assert.Equal(hit, e.SampleIndex);
        Assert.All(r.Output, v => Assert.True(float.IsFinite(v)));
    }

    // ---------- nits ----------

    [Fact]
    public void VoicePool_FullPoolFallback_NeverHardCutsAPlayingVoice()
    {
        var pool = new VoicePool();
        var data = DcSample(Fs);
        const int max = VoicePool.MaxSupportedVoices;  // 16 playing + up to 16 fading = all 32 slots
        for (int i = 0; i < max * 2; i++) pool.Start(data, 1f, false, max, fadeSamples: Fs);
        Assert.Equal((max, max), pool.Counts());

        pool.Start(data, 1f, false, max, fadeSamples: Fs); // pool full: must replace a FADING voice
        Assert.Equal((max, max), pool.Counts());
    }

    [Fact]
    public void SettingsChangeMidDetect_DoesNotSkipDebounce()
    {
        // A 1-sample click on the last sample of block 0 starts DETECT; at block 1 DetectTimeMs drops to 0.
        // The window frozen at the crossing keeps the 0.5 ms debounce, so the click is still rejected.
        var engine = new TriggerEngine(Fs, Defaults with { ScanMs = 0f, HighPassHz = 0f });
        var b0 = new float[128];
        b0[127] = Db.ToLinear(-18f);
        engine.Process(b0, new float[128]);
        engine.UpdateSettings(engine.Settings with { DetectTimeMs = 0f });
        engine.Process(new float[128], new float[128]);
        engine.Process(new float[128], new float[128]);
        Assert.False(engine.TryDequeueEvent(out _));
    }

    [Fact]
    public void Wav_Extensible_Pcm24Stereo_IsRead()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            bw.Write("RIFF"u8); bw.Write(0u); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(40u);
            bw.Write((ushort)0xFFFE); bw.Write((ushort)2); bw.Write(44100); bw.Write(44100 * 6); bw.Write((ushort)6); bw.Write((ushort)24);
            bw.Write((ushort)22); bw.Write((ushort)24); bw.Write(3u);                     // cbSize, valid bits, mask
            bw.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());     // KSDATAFORMAT_SUBTYPE_PCM
            bw.Write("data"u8); bw.Write(6u);
            int l = 4194304, r = -4194304;                                                 // +0.5 / -0.5
            bw.Write((byte)l); bw.Write((byte)(l >> 8)); bw.Write((byte)(l >> 16));
            bw.Write((byte)r); bw.Write((byte)(r >> 8)); bw.Write((byte)(r >> 16));
        }
        ms.Position = 0;
        var w = WavFile.Read(ms, 0);
        Assert.Equal(2, w.Channels);
        Assert.Equal(24, w.BitsPerSample);
        Assert.False(w.IsFloat);
        Assert.Equal(0.5f, w.Mono[0], 6);
        ms.Position = 0;
        Assert.Equal(-0.5f, WavFile.Read(ms, 1).Mono[0], 6);
    }

    [Fact]
    public void Wav_Extensible_Float32_IsRead()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            bw.Write("RIFF"u8); bw.Write(0u); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(40u);
            bw.Write((ushort)0xFFFE); bw.Write((ushort)1); bw.Write(48000); bw.Write(48000 * 4); bw.Write((ushort)4); bw.Write((ushort)32);
            bw.Write((ushort)22); bw.Write((ushort)32); bw.Write(4u);
            bw.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray());     // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            bw.Write("data"u8); bw.Write(8u); bw.Write(0.25f); bw.Write(-0.75f);
        }
        ms.Position = 0;
        var w = WavFile.Read(ms);
        Assert.True(w.IsFloat);
        Assert.Equal(new[] { 0.25f, -0.75f }, w.Mono);
    }

    [Fact]
    public void Wav_FmtChunkShorterThan16Bytes_Rejected()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            bw.Write("RIFF"u8); bw.Write(0u); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(14u);
            bw.Write((ushort)1); bw.Write((ushort)1); bw.Write(48000); bw.Write(96000); bw.Write((ushort)2);
            bw.Write("data"u8); bw.Write(2u); bw.Write((short)100);
        }
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => WavFile.Read(ms));
    }
}
