namespace SampleDrum.Core.Simulation;

/// <summary>
/// Settings of the stereo preview mix (headphones/speakers) of the four returns, optionally with the mics underneath.
/// Immutable: swap a whole instance to change it while audio runs.
/// </summary>
public sealed record PreviewMixSettings
{
    /// <summary>Master volume of the preview (dB).</summary>
    public float VolumeDb { get; init; } = -6f;

    /// <summary>Which pieces' returns are in the mix (index = DrumPiece). Missing entries = included.</summary>
    public IReadOnlyList<bool> IncludePiece { get; init; } = [true, true, true, true];

    /// <summary>Level of the mic signals under the returns (dB); null = mics not in the preview.</summary>
    public float? MicLevelDb { get; init; }

    public static PreviewMixSettings Default { get; } = new();

    public bool Includes(int piece) => piece >= IncludePiece.Count || IncludePiece[piece];
}

/// <summary>
/// Allocation-free stereo mixing of up to four mono returns (+ optional mics). Fixed pan so pieces are easy to tell
/// apart: Bumbo centre, Caixa slightly left, Tom right, Surdo further right. Constant-power pan.
/// </summary>
public static class PreviewMix
{
    /// <summary>Pan per piece, -1 (left) .. +1 (right).</summary>
    public static readonly float[] Pan = [0f, -0.3f, 0.35f, 0.6f];

    private static readonly float[] GainL = new float[4];
    private static readonly float[] GainR = new float[4];

    static PreviewMix()
    {
        for (int p = 0; p < 4; p++)
        {
            float a = (Pan[p] + 1f) * MathF.PI / 4f; // 0 .. pi/2
            GainL[p] = MathF.Cos(a) * MathF.Sqrt(2f) * 0.5f;
            GainR[p] = MathF.Sin(a) * MathF.Sqrt(2f) * 0.5f;
        }
    }

    /// <summary>
    /// Mixes <paramref name="frames"/> frames into <paramref name="stereoInterleaved"/> (L,R,L,R..., overwritten).
    /// Audio-thread safe: no allocation, no lock. <paramref name="mics"/> may be null.
    /// </summary>
    public static void MixInterleaved(float[][] returns, float[][]? mics, int frames, Span<float> stereoInterleaved,
        PreviewMixSettings settings)
    {
        stereoInterleaved = stereoInterleaved[..(frames * 2)];
        stereoInterleaved.Clear();
        float vol = Db.ToLinear(settings.VolumeDb);
        float micGain = settings.MicLevelDb is float m ? Db.ToLinear(m) : 0f;
        int n = Math.Min(4, returns.Length);
        for (int p = 0; p < n; p++)
        {
            bool inc = settings.Includes(p);
            float gl = GainL[p] * vol, gr = GainR[p] * vol;
            float[] r = returns[p];
            float[]? mic = mics is not null && p < mics.Length && micGain > 0f ? mics[p] : null;
            if (!inc && mic is null) continue;
            for (int i = 0; i < frames; i++)
            {
                float s = (inc ? r[i] : 0f) + (mic is null ? 0f : mic[i] * micGain);
                stereoInterleaved[2 * i] += s * gl;
                stereoInterleaved[2 * i + 1] += s * gr;
            }
        }
        for (int i = 0; i < stereoInterleaved.Length; i++)
        {
            float v = stereoInterleaved[i];
            stereoInterleaved[i] = v > 1f ? 1f : v < -1f ? -1f : v;
        }
    }

    /// <summary>Offline: whole-buffer stereo mix (allocates). Returns [left, right].</summary>
    public static float[][] Mix(float[][] returns, float[][]? mics, PreviewMixSettings settings)
    {
        int len = returns.Max(r => r.Length);
        var inter = new float[len * 2];
        MixInterleaved(returns, mics, len, inter, settings);
        var l = new float[len];
        var r = new float[len];
        for (int i = 0; i < len; i++) { l[i] = inter[2 * i]; r[i] = inter[2 * i + 1]; }
        return [l, r];
    }
}
