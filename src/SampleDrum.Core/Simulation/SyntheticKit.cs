namespace SampleDrum.Core.Simulation;

/// <summary>
/// Deterministic synthetic drum sounds for the 4-piece harness. Piece index order = DrumPiece
/// (0 Bumbo/Kick, 1 Caixa/Snare, 2 Tom, 3 Surdo/FloorTom).
/// Two families: "mic" hits (what the microphone picks up, used to build the scenario tracks) and
/// "samples" (the one-shots the engine plays back), each with a distinct timbre so the returns are easy to tell apart.
/// </summary>
public static class SyntheticKit
{
    public const int PieceCount = 4;

    /// <summary>pt-BR names, index = DrumPiece.</summary>
    public static readonly string[] PieceNames = ["Bumbo", "Caixa", "Tom", "Surdo"];

    /// <summary>File-name slugs (retorno-bumbo.wav, ...).</summary>
    public static readonly string[] PieceSlugs = ["bumbo", "caixa", "tom", "surdo"];

    /// <summary>One mic hit of <paramref name="piece"/>, normalized to <paramref name="peakLin"/>.</summary>
    public static float[] MicHit(int piece, int sampleRate, float peakLin, float sustainMs, int seed) => piece switch
    {
        0 => SyntheticSignals.Kick(sampleRate, peakLin, sustainMs, seed),                                 // 120 -> 50 Hz sweep + click
        1 => Snare(sampleRate, peakLin, sustainMs, seed),
        2 => Tonal(sampleRate, peakLin, sustainMs, seed, startHz: 190f, endHz: 140f, clickLevel: 0.35f),  // rack tom
        3 => Tonal(sampleRate, peakLin, sustainMs, seed, startHz: 120f, endHz: 85f, clickLevel: 0.3f),    // floor tom
        _ => throw new ArgumentOutOfRangeException(nameof(piece)),
    };

    /// <summary>The one-shot sample the engine plays for <paramref name="piece"/> (peak -1 dBFS, faded tail).</summary>
    public static float[] Sample(int piece, int sampleRate)
    {
        float peak = EngineParams.DbToLin(-1f);
        float[] raw = piece switch
        {
            0 => SyntheticSignals.KickSample(sampleRate),
            1 => Snare(sampleRate, peak, 70f, seed: 11, toneHz: 230f, noiseMix: 0.75f),
            2 => Tonal(sampleRate, peak, 140f, seed: 12, startHz: 230f, endHz: 165f, clickLevel: 0.25f),
            3 => Tonal(sampleRate, peak, 190f, seed: 13, startHz: 140f, endHz: 95f, clickLevel: 0.2f),
            _ => throw new ArgumentOutOfRangeException(nameof(piece)),
        };
        int maxLen = (int)(sampleRate * (piece == 0 ? 0.4 : 0.6));
        var s = raw.Length > maxLen ? raw[..maxLen] : raw;
        int fade = (int)(0.005 * sampleRate);
        for (int i = 0; i < fade && i < s.Length; i++) s[s.Length - 1 - i] *= (float)i / fade;
        return s;
    }

    /// <summary>Snare: band-limited noise (the wires) plus a short tone (the head).</summary>
    public static float[] Snare(int sampleRate, float peakLin, float sustainMs, int seed, float toneHz = 190f, float noiseMix = 0.65f)
    {
        double fs = sampleRate;
        int len = (int)Math.Min(fs * 2, Math.Ceiling(sustainMs * 0.001 * fs * 12) + fs * 0.005);
        var h = new float[len];
        var rng = new Random(seed);
        double toneTau = Math.Max(1.0, sustainMs * 0.0006 * fs);
        double noiseTau = Math.Max(1.0, sustainMs * 0.001 * fs);
        double hp = 0, prevIn = 0, lp = 0;
        double a = Math.Exp(-2 * Math.PI * 1500 / fs); // 1-pole HP ~1.5 kHz
        double b = Math.Exp(-2 * Math.PI * 7000 / fs); // 1-pole LP ~7 kHz
        for (int i = 0; i < len; i++)
        {
            double n = rng.NextDouble() * 2 - 1;
            hp = a * (hp + n - prevIn); prevIn = n;
            lp = (1 - b) * hp + b * lp;
            double tone = Math.Sin(2 * Math.PI * toneHz * i / fs) * Math.Exp(-i / toneTau);
            h[i] = (float)(noiseMix * lp * 3.0 * Math.Exp(-i / noiseTau) + (1 - noiseMix) * tone);
        }
        return Normalize(h, peakLin);
    }

    /// <summary>Tom-like: sine with a short downward pitch sweep, exponential decay and a small stick click.</summary>
    public static float[] Tonal(int sampleRate, float peakLin, float sustainMs, int seed, float startHz, float endHz, float clickLevel)
        => SyntheticSignals.Kick(sampleRate, peakLin, sustainMs, seed, startHz, endHz, clickLevel);

    private static float[] Normalize(float[] h, float peakLin)
    {
        float max = 0f;
        foreach (var v in h) max = Math.Max(max, Math.Abs(v));
        float g = max > 0 ? peakLin / max : 0f;
        for (int i = 0; i < h.Length; i++) h[i] *= g;
        return h;
    }
}
