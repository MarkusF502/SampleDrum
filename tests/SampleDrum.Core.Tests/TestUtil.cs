using SampleDrum.Core;

namespace SampleDrum.Core.Tests;

internal static class TestUtil
{
    public const int Fs = 48000;

    /// <summary>Onset timing tolerance used by the tests: ±1 ms (48 samples at 48 kHz).</summary>
    public const long ToleranceSamples = 48;

    public static long Ms(double ms) => (long)Math.Round(ms * Fs / 1000.0);

    /// <summary>
    /// Settings for tests driven by single-sample impulses: no debounce (an impulse would be rejected as a
    /// spike), no filters, no scan, fixed velocity (gain 1), no re-trigger.
    /// </summary>
    public static readonly TriggerSettings Impulsive = TriggerSettings.Default with
    {
        DetectTimeMs = 0f,
        ScanMs = 0f,
        HighPassHz = 0f,
        FixedVelocity = true,
        RetriggerRiseDb = 0f,
    };

    /// <summary>Silent buffer with a noise floor (deterministic).</summary>
    public static float[] NoiseFloor(double seconds, int seed = 1, float rmsDb = -60f)
        => SyntheticSignals.Noise((int)(seconds * Fs), rmsDb, seed);

    public static void AddKick(float[] buf, long start, float levelDb, float sustainMs, int seed = 11,
        float startHz = 120f, float endHz = 50f)
        => SyntheticSignals.Add(buf, SyntheticSignals.Kick(Fs, Db.ToLinear(levelDb), sustainMs, seed, startHz, endHz), start);

    /// <summary>Cosine burst starting at full amplitude (phase 0), so the onset is exactly <paramref name="start"/>.</summary>
    public static void AddCosBurst(float[] buf, long start, float levelDb, double hz, double ms)
    {
        float a = Db.ToLinear(levelDb);
        long n = Ms(ms);
        for (long i = 0; i < n && start + i < buf.Length; i++)
            buf[start + i] += a * (float)Math.Cos(2 * Math.PI * hz * i / Fs);
    }

    /// <summary>Snare/tom-like bleed: 400 Hz tone + white noise, 80 ms decay, normalized to the peak level.</summary>
    public static void AddSnare(float[] buf, long start, float levelDb, int seed = 5)
    {
        var rng = new Random(seed);
        int n = (int)Ms(400);
        var h = new float[n];
        float max = 0f;
        for (int i = 0; i < n; i++)
        {
            double env = Math.Exp(-i / (0.08 * Fs));
            h[i] = (float)((0.6 * Math.Sin(2 * Math.PI * 400 * i / Fs) + 0.6 * (rng.NextDouble() * 2 - 1)) * env);
            max = Math.Max(max, Math.Abs(h[i]));
        }
        float g = Db.ToLinear(levelDb) / max;
        for (int i = 0; i < n; i++) h[i] *= g;
        SyntheticSignals.Add(buf, h, start);
    }

    /// <summary>A sample of constant 1.0 values, so the output reveals exactly when and how loud voices start.</summary>
    public static float[] DcSample(int length) => Enumerable.Repeat(1f, length).ToArray();

    public static OfflineResult Run(float[] mic, TriggerSettings s, int block = 128, float[]? sample = null, bool enabled = true)
        => OfflineProcessor.Run(mic, sample ?? SyntheticSignals.KickSample(Fs), Fs, s, block, enabled);

    /// <summary>Runs the bare detector over mic[0..end) and returns its state at the end.</summary>
    public static DetectorState StateBefore(float[] mic, long end, TriggerSettings s)
    {
        var d = new OnsetDetector(Fs, s);
        for (long i = 0; i < end; i++) d.Step(mic[i], i, out _);
        return d.State;
    }

    public static int FirstNonZero(float[] x)
    {
        for (int i = 0; i < x.Length; i++) if (x[i] != 0f) return i;
        return -1;
    }
}
