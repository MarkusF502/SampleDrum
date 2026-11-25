using SampleDrum.Core;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

public class EngineTests
{
    private static readonly TriggerSettings Defaults = TriggerSettings.Default;

    [Fact]
    public void Disabled_IsExactSilence_AndNoEvents()
    {
        var (mic, _) = SyntheticSignals.MicTrack(Fs);
        var r = Run(mic, Defaults, enabled: false);
        Assert.Empty(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ManualTest_PlaysEvenWhenDisabled_AtBlockStart()
    {
        var sample = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        var engine = new TriggerEngine(Fs, Defaults with { OutputGainDb = 0f }) { Enabled = false };
        engine.SetSample(sample);

        var input = new float[64];
        var output = new float[64];
        engine.Process(input, output);
        Assert.All(output, v => Assert.Equal(0f, v));

        engine.RequestTest(0.5f);
        Array.Clear(output);
        engine.Process(input, output);
        for (int i = 0; i < sample.Length; i++) Assert.Equal(sample[i] * 0.5f, output[i], 6);
        for (int i = sample.Length; i < output.Length; i++) Assert.Equal(0f, output[i]);

        Assert.True(engine.TryDequeueEvent(out var e));
        Assert.True(e.IsManualTest);
        Assert.Equal(64, e.SampleIndex);
        Assert.Equal(0.5f, e.Velocity);
        Assert.False(engine.TryDequeueEvent(out _));

        // After the test finished and with nothing pending, disabled output is exact silence again.
        Array.Clear(output);
        engine.Process(input, output);
        Assert.All(output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ManualTest_IsConsumedOnce()
    {
        var engine = new TriggerEngine(Fs, Defaults) { Enabled = false };
        engine.SetSample(new[] { 1f });
        engine.RequestTest();
        engine.RequestTest();
        var o = new float[16];
        engine.Process(new float[16], o);
        Assert.Equal(1f, o[0]);
        Array.Clear(o);
        engine.Process(new float[16], o);
        Assert.All(o, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Disabling_FadesOutAutomaticVoices_ThenSilence()
    {
        var engine = new TriggerEngine(Fs, Defaults with { ScanMs = 0f, Dynamics = 0f, StealFadeMs = 1f });
        engine.SetSample(DcSample(Fs));
        var input = new float[128];
        input[10] = 0.5f;
        var o = new float[128];
        engine.Process(input, o);
        Assert.Equal(1f, o[127]);

        engine.Enabled = false;
        Array.Clear(input);
        Array.Clear(o);
        engine.Process(input, o);                      // 1 ms fade = 48 samples
        Assert.True(o[0] < 1f && o[0] > 0.9f);
        Assert.Equal(0f, o[60]);
        Array.Clear(o);
        engine.Process(input, o);
        Assert.All(o, v => Assert.Equal(0f, v));
        Assert.Equal(0, engine.ActiveVoiceCount);
    }

    [Fact]
    public void Enabled_NoSampleLoaded_EventsStillReported_OutputSilent()
    {
        var engine = new TriggerEngine(Fs, Defaults);
        var mic = new float[4800];
        mic[100] = 0.5f;
        var r = OfflineProcessor.Run(engine, mic, 128);
        Assert.Single(r.Events);
        Assert.All(r.Output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void VoicePool_StealsOldest_WithFade_KeepsMaxVoices()
    {
        var s = Defaults with { MaxVoices = 2, ScanMs = 0f, Dynamics = 0f, MinIntervalMs = 0f, StealFadeMs = 2f };
        var mic = new float[Fs];
        long[] hits = { 1000, 6000, 11000 };               // impulses, envelope re-arms in between
        foreach (var h in hits) mic[h] = 0.5f;
        var r = Run(mic, s, 128, DcSample(Fs));

        Assert.Equal(3, r.Events.Count);
        Assert.Equal(1f, r.Output[5000]);
        Assert.Equal(2f, r.Output[10000]);
        // Third voice starts: the oldest fades over 2 ms (96 samples) instead of being cut.
        Assert.InRange(r.Output[11000], 2.9f, 3f);
        Assert.InRange(r.Output[11048], 2.4f, 2.6f);
        Assert.Equal(2f, r.Output[11200]);
        // No sample-to-sample jump larger than one voice entering (1.0) - i.e. no click from the steal.
        for (int i = 1; i < r.Output.Length; i++)
            Assert.True(Math.Abs(r.Output[i] - r.Output[i - 1]) <= 1.0001f, $"jump at {i}");
    }

    [Fact]
    public void UpdateSettings_AppliedAtNextBlock()
    {
        var engine = new TriggerEngine(Fs, Defaults with { ScanMs = 0f });
        var block = new float[128];
        var o = new float[128];
        block[5] = Db.ToLinear(-20f);

        engine.UpdateSettings(Defaults with { ThresholdDb = -10f, ScanMs = 0f });
        engine.Process(block, o);
        Assert.False(engine.TryDequeueEvent(out _));
        Assert.Equal(-10f, engine.Settings.ThresholdDb);

        // Lowering the threshold below the current envelope must not fire by itself.
        engine.UpdateSettings(Defaults with { ThresholdDb = -30f, ScanMs = 0f });
        var silence = new float[Fs / 4];                 // let the envelope decay and re-arm
        engine.Process(silence, new float[silence.Length]);
        Assert.False(engine.TryDequeueEvent(out _));
        engine.Process(block, o);
        Assert.True(engine.TryDequeueEvent(out var e));
        Assert.Equal(128 + silence.Length + 5, e.SampleIndex);
    }

    [Fact]
    public void Settings_AreNormalized()
    {
        var s = new TriggerSettings { ThresholdDb = -20f, RearmDb = -10f, MaxVoices = 100, Dynamics = 3f, ScanMs = float.NaN }.Normalized();
        Assert.Equal(-20f, s.RearmDb);
        Assert.Equal(16, s.MaxVoices);
        Assert.Equal(1f, s.Dynamics);
        Assert.Equal(2f, s.ScanMs);
    }

    [Fact]
    public void InputPeak_ReadAndReset()
    {
        var engine = new TriggerEngine(Fs, Defaults);
        var x = new float[64];
        x[3] = -0.75f;
        x[9] = 0.5f;
        engine.Process(x, new float[64]);
        Assert.Equal(0.75f, engine.ReadAndResetInputPeak());
        Assert.Equal(0f, engine.ReadAndResetInputPeak());
    }

    [Fact]
    public void EventQueue_DropsWhenFull_WithoutBlocking()
    {
        var engine = new TriggerEngine(Fs, Defaults);
        engine.SetSample(new[] { 1f });
        for (int i = 0; i < TriggerEngine.EventCapacity + 10; i++)
        {
            engine.RequestTest();
            engine.Process(new float[8], new float[8]);
        }
        Assert.Equal(10, engine.DroppedEvents);
        int n = 0;
        while (engine.TryDequeueEvent(out _)) n++;
        Assert.Equal(TriggerEngine.EventCapacity, n);
    }

    [Fact]
    public void Process_DoesNotAllocate()
    {
        var (mic, _) = SyntheticSignals.MicTrack(Fs);
        var engine = new TriggerEngine(Fs, Defaults with { HighPassHz = 30f });
        engine.SetSample(SyntheticSignals.KickSample(Fs));
        var output = new float[128];

        void RunAll()
        {
            for (int start = 0; start + 128 <= mic.Length; start += 128)
            {
                output.AsSpan().Clear();
                engine.Process(mic.AsSpan(start, 128), output);
                if (start % (128 * 64) == 0) engine.RequestTest(0.5f);
            }
        }

        RunAll(); // warm-up (JIT, tiering)
        while (engine.TryDequeueEvent(out _)) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunAll();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SetSample_SwapDoesNotAffectPlayingVoice()
    {
        var engine = new TriggerEngine(Fs, Defaults) { Enabled = false };
        engine.SetSample(DcSample(256));
        engine.RequestTest();
        var o = new float[128];
        engine.Process(new float[128], o);
        engine.SetSample(new[] { 0.25f });
        Array.Clear(o);
        engine.Process(new float[128], o);
        Assert.All(o, v => Assert.Equal(1f, v));     // old voice keeps its sample
        engine.RequestTest();
        Array.Clear(o);
        engine.Process(new float[128], o);
        Assert.Equal(0.25f, o[0]);
    }
}
