namespace SampleDrum.Core;

/// <summary>Biquad coefficients (normalized by a0), transposed direct form II.</summary>
internal readonly struct BiquadCoefs
{
    public readonly bool Enabled;
    public readonly double B0, B1, B2, A1, A2;

    private BiquadCoefs(double b0, double b1, double b2, double a1, double a2)
    {
        Enabled = true; B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
    }

    // RBJ audio-EQ cookbook formulas, Q = 1/sqrt(2) (2nd-order Butterworth, 12 dB/oct).
    public static BiquadCoefs HighPass(double fc, double fs)
    {
        if (!(fc > 0 && fc < fs * 0.45)) return default;
        double w0 = 2 * Math.PI * fc / fs, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5)), a0 = 1 + alpha;
        return new((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    public static BiquadCoefs LowPass(double fc, double fs)
    {
        if (!(fc > 0 && fc < fs * 0.45)) return default;
        double w0 = 2 * Math.PI * fc / fs, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5)), a0 = 1 + alpha;
        return new((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }
}

/// <summary>
/// Immutable, pre-computed per-sample-rate coefficients derived from <see cref="TriggerSettings"/>.
/// Built off the audio thread and swapped in by reference so <see cref="TriggerEngine.Process"/>
/// never allocates or does expensive math to apply new settings.
/// </summary>
internal sealed class EngineParams
{
    public readonly TriggerSettings Settings;

    public readonly float ThresholdDb;
    public readonly float ThresholdLin;
    public readonly float ThresholdSq;
    public readonly float RearmLin;
    public readonly int RearmSamples;
    public readonly int MinIntervalSamples;
    public readonly int DetectSamples;
    /// <summary>Velocity/trigger window = max(detect, scan) samples; the trigger fires at crossing + this.</summary>
    public readonly int WindowSamples;
    public readonly float RetriggerRiseLin;   // 0 = off
    public readonly float EnvelopeReleaseCoef;
    public readonly float VelocityRangeDb;
    public readonly float MinGainDb;
    public readonly bool FixedVelocity;
    public readonly float OutputGainLin;
    public readonly int MaxVoices;
    public readonly int StealFadeSamples;
    public readonly BiquadCoefs HighPass;
    public readonly BiquadCoefs LowPass;
    /// <summary>Gentle fixed high-pass on the velocity path (removes rumble/DC, keeps the kick's first half-cycle).</summary>
    public readonly BiquadCoefs VelocityHighPass;
    public const double VelocityHighPassHz = 20.0;

    public EngineParams(int sampleRate, TriggerSettings settings)
    {
        var s = settings.Normalized();
        Settings = s;
        double fs = sampleRate;

        ThresholdDb = s.ThresholdDb;
        ThresholdLin = DbToLin(s.ThresholdDb);
        ThresholdSq = ThresholdLin * ThresholdLin;
        RearmLin = DbToLin(s.RearmDb);
        RearmSamples = MsToSamples(s.RearmTimeMs, fs);
        MinIntervalSamples = MsToSamples(s.MinIntervalMs, fs);
        DetectSamples = MsToSamples(s.DetectTimeMs, fs);
        WindowSamples = Math.Max(DetectSamples, MsToSamples(s.ScanMs, fs));
        RetriggerRiseLin = s.RetriggerRiseDb > 0 ? DbToLin(s.RetriggerRiseDb) : 0f;
        EnvelopeReleaseCoef = (float)Math.Exp(-1.0 / (s.EnvelopeReleaseMs * 0.001 * fs));
        VelocityRangeDb = s.VelocityRangeDb;
        MinGainDb = s.MinGainDb;
        FixedVelocity = s.FixedVelocity;
        OutputGainLin = DbToLin(s.OutputGainDb);
        MaxVoices = s.MaxVoices;
        StealFadeSamples = MsToSamples(s.StealFadeMs, fs);
        HighPass = BiquadCoefs.HighPass(s.HighPassHz, fs);
        LowPass = BiquadCoefs.LowPass(s.LowPassHz, fs);
        VelocityHighPass = BiquadCoefs.HighPass(VelocityHighPassHz, fs);
    }

    /// <summary>Velocity 0..1 from a linear detector peak (linear in dB above the threshold).</summary>
    public float VelocityFromPeak(float peak)
    {
        if (peak <= 0f) return 0f;
        float db = 20f * MathF.Log10(peak);
        float v = (db - ThresholdDb) / VelocityRangeDb;
        return v < 0f ? 0f : v > 1f ? 1f : v;
    }

    /// <summary>Linear voice gain for an automatic trigger: MinGainDb at velocity 0, 0 dB at velocity 1.</summary>
    public float GainFromVelocity(float velocity)
    {
        if (FixedVelocity) return OutputGainLin;
        float db = (1f - velocity) * MinGainDb;
        return MathF.Pow(10f, db / 20f) * OutputGainLin;
    }

    public static float DbToLin(float db) => MathF.Pow(10f, db / 20f);

    public static int MsToSamples(float ms, double fs) => (int)Math.Round(ms * 0.001 * fs);
}
