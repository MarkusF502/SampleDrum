namespace SampleDrum.Core;

/// <summary>A hit placed in a synthetic mic track.</summary>
/// <param name="StartSample">Sample index where the hit starts (the expected onset).</param>
/// <param name="LevelDb">Peak level of the hit (dBFS).</param>
/// <param name="SustainMs">Decay time constant of the low-frequency body (ms).</param>
/// <param name="Label">Short description.</param>
public sealed record SyntheticHit(long StartSample, float LevelDb, float SustainMs, string Label);

/// <summary>
/// Deterministic synthetic signals for offline tests (no randomness beyond seeded <see cref="Random"/>).
/// Kick model: short noise "beater click" (~1.5 ms) + sine body with a downward pitch sweep
/// (~120 Hz -> 50 Hz) and an exponential amplitude decay; the whole hit is normalized to the requested peak.
/// </summary>
public static class SyntheticSignals
{
    /// <summary>Block boundary shared by 32, 64, 128, 256 and 480 sample blocks (245760 = 512 * 480 = 960 * 256).</summary>
    public const long CommonBlockBoundary = 245_760;

    /// <summary>Generates one normalized kick hit (peak = <paramref name="peakLin"/>).</summary>
    public static float[] Kick(int sampleRate, float peakLin, float sustainMs, int seed,
        float startHz = 120f, float endHz = 50f, float clickLevel = 0.5f)
    {
        double fs = sampleRate;
        int len = (int)Math.Min(fs * 4, Math.Ceiling(sustainMs * 0.001 * fs * 14) + fs * 0.005);
        var h = new float[len];
        var rng = new Random(seed);
        double phase = 0;
        double sweepTau = 0.012 * fs;
        double bodyTau = Math.Max(1.0, sustainMs * 0.001 * fs);
        double clickTau = 0.0005 * fs;
        for (int i = 0; i < len; i++)
        {
            double f = endHz + (startHz - endHz) * Math.Exp(-i / sweepTau);
            double body = Math.Sin(phase) * Math.Exp(-i / bodyTau);
            phase += 2 * Math.PI * f / fs;
            double click = i < 0.003 * fs ? clickLevel * (rng.NextDouble() * 2 - 1) * Math.Exp(-i / clickTau) : 0;
            h[i] = (float)(body + click);
        }
        float max = 0f;
        foreach (var v in h) max = Math.Max(max, Math.Abs(v));
        float g = max > 0 ? peakLin / max : 0f;
        for (int i = 0; i < len; i++) h[i] *= g;
        return h;
    }

    /// <summary>Adds <paramref name="hit"/> into <paramref name="buffer"/> starting at <paramref name="start"/>.</summary>
    public static void Add(float[] buffer, float[] hit, long start)
    {
        for (int i = 0; i < hit.Length; i++)
        {
            long k = start + i;
            if (k < 0) continue;
            if (k >= buffer.Length) break;
            buffer[k] += hit[i];
        }
    }

    /// <summary>White noise with the given RMS level (dBFS).</summary>
    public static float[] Noise(int length, float rmsDb, int seed)
    {
        var rng = new Random(seed);
        // Uniform [-a, a] has RMS a/sqrt(3).
        float a = EngineParams.DbToLin(rmsDb) * MathF.Sqrt(3f);
        var n = new float[length];
        for (int i = 0; i < length; i++) n[i] = (float)(rng.NextDouble() * 2 - 1) * a;
        return n;
    }

    /// <summary>Synthetic one-shot kick sample (~400 ms, peak -1 dBFS) to be played by the engine.</summary>
    public static float[] KickSample(int sampleRate)
    {
        var k = Kick(sampleRate, EngineParams.DbToLin(-1f), 90f, seed: 7, startHz: 160f, endHz: 48f, clickLevel: 0.35f);
        int len = Math.Min(k.Length, (int)(0.4 * sampleRate));
        var s = new float[len];
        Array.Copy(k, s, len);
        int fade = (int)(0.005 * sampleRate);
        for (int i = 0; i < fade && i < len; i++) s[len - 1 - i] *= (float)i / fade;
        return s;
    }

    /// <summary>
    /// The standard synthetic kick-mic track (~9 s): -60 dBFS noise floor; hard, soft and medium hits;
    /// one hit with a long low-frequency sustain; two close hits 100 ms apart; a hit that starts 3 samples
    /// before <see cref="CommonBlockBoundary"/>; and a velocity ladder (-24/-18/-12/-6 dBFS).
    /// </summary>
    public static (float[] Mic, IReadOnlyList<SyntheticHit> Hits) MicTrack(int sampleRate)
    {
        var mic = Noise((int)(9.0 * sampleRate), -60f, seed: 1);
        var hits = new List<SyntheticHit>
        {
            new(Ms(500, sampleRate), -3f, 120f, "forte"),
            new(Ms(1200, sampleRate), -20f, 80f, "suave"),
            new(Ms(2000, sampleRate), -6f, 300f, "sustain longo"),
            new(Ms(3500, sampleRate), -6f, 12f, "proximo-1"),
            new(Ms(3600, sampleRate), -6f, 12f, "proximo-2 (+100 ms)"),
            new(CommonBlockBoundary - 3, -8f, 60f, "atravessa fronteira de bloco"),
            new(Ms(6000, sampleRate), -24f, 60f, "escada -24 dB"),
            new(Ms(6500, sampleRate), -18f, 60f, "escada -18 dB"),
            new(Ms(7000, sampleRate), -12f, 60f, "escada -12 dB"),
            new(Ms(7500, sampleRate), -6f, 60f, "escada -6 dB"),
        };
        int seed = 100;
        foreach (var h in hits)
            Add(mic, Kick(sampleRate, EngineParams.DbToLin(h.LevelDb), h.SustainMs, seed++), h.StartSample);
        return (mic, hits);
    }

    private static long Ms(double ms, int sampleRate) => (long)Math.Round(ms * 0.001 * sampleRate);
}
