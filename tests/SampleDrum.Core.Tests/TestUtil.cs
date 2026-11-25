using SampleDrum.Core;

namespace SampleDrum.Core.Tests;

internal static class TestUtil
{
    public const int Fs = 48000;

    /// <summary>Onset timing tolerance used by the tests: ±1 ms (48 samples at 48 kHz).</summary>
    public const long ToleranceSamples = 48;

    public static long Ms(double ms) => (long)Math.Round(ms * Fs / 1000.0);

    /// <summary>Silent buffer with a -60 dBFS noise floor (deterministic).</summary>
    public static float[] NoiseFloor(double seconds, int seed = 1)
        => SyntheticSignals.Noise((int)(seconds * Fs), -60f, seed);

    public static void AddKick(float[] buf, long start, float levelDb, float sustainMs, int seed = 11)
        => SyntheticSignals.Add(buf, SyntheticSignals.Kick(Fs, Db.ToLinear(levelDb), sustainMs, seed), start);

    /// <summary>A sample of constant 1.0 values, so the output reveals exactly when and how loud voices start.</summary>
    public static float[] DcSample(int length) => Enumerable.Repeat(1f, length).ToArray();

    public static OfflineResult Run(float[] mic, TriggerSettings s, int block = 128, float[]? sample = null, bool enabled = true)
        => OfflineProcessor.Run(mic, sample ?? SyntheticSignals.KickSample(Fs), Fs, s, block, enabled);

    public static int FirstNonZero(float[] x)
    {
        for (int i = 0; i < x.Length; i++) if (x[i] != 0f) return i;
        return -1;
    }
}
