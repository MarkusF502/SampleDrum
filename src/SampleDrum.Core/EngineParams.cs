namespace SampleDrum.Core;

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
    public readonly float RearmLin;
    public readonly int RearmSamples;
    public readonly int MinIntervalSamples;
    public readonly int ScanSamples;
    public readonly float EnvelopeReleaseCoef;
    public readonly float VelocityRangeDb;
    public readonly float Dynamics;
    public readonly float OutputGainLin;
    public readonly int MaxVoices;
    public readonly int StealFadeSamples;

    // High-pass biquad (RBJ cookbook, Q = 1/sqrt(2)), transposed direct form II, normalized by a0.
    public readonly bool HighPassEnabled;
    public readonly double HpB0, HpB1, HpB2, HpA1, HpA2;

    public EngineParams(int sampleRate, TriggerSettings settings)
    {
        var s = settings.Normalized();
        Settings = s;
        double fs = sampleRate;

        ThresholdDb = s.ThresholdDb;
        ThresholdLin = DbToLin(s.ThresholdDb);
        RearmLin = DbToLin(s.RearmDb);
        RearmSamples = MsToSamples(s.RearmTimeMs, fs);
        MinIntervalSamples = MsToSamples(s.MinIntervalMs, fs);
        ScanSamples = MsToSamples(s.ScanMs, fs);
        EnvelopeReleaseCoef = (float)Math.Exp(-1.0 / (s.EnvelopeReleaseMs * 0.001 * fs));
        VelocityRangeDb = s.VelocityRangeDb;
        Dynamics = s.Dynamics;
        OutputGainLin = DbToLin(s.OutputGainDb);
        MaxVoices = s.MaxVoices;
        StealFadeSamples = MsToSamples(s.StealFadeMs, fs);

        double fc = s.HighPassHz;
        HighPassEnabled = fc > 0 && fc < fs * 0.45;
        if (HighPassEnabled)
        {
            double w0 = 2 * Math.PI * fc / fs;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
            double a0 = 1 + alpha;
            HpB0 = (1 + cos) / 2 / a0;
            HpB1 = -(1 + cos) / a0;
            HpB2 = (1 + cos) / 2 / a0;
            HpA1 = -2 * cos / a0;
            HpA2 = (1 - alpha) / a0;
        }
    }

    /// <summary>Velocity 0..1 from a linear detector peak.</summary>
    public float VelocityFromPeak(float peak)
    {
        if (peak <= 0f) return 0f;
        float db = 20f * MathF.Log10(peak);
        float v = (db - ThresholdDb) / VelocityRangeDb;
        return v < 0f ? 0f : v > 1f ? 1f : v;
    }

    /// <summary>Linear voice gain for an automatic trigger with the given velocity.</summary>
    public float GainFromVelocity(float velocity)
    {
        float db = -(1f - velocity) * Dynamics * VelocityRangeDb;
        return MathF.Pow(10f, db / 20f) * OutputGainLin;
    }

    public static float DbToLin(float db) => MathF.Pow(10f, db / 20f);

    public static float LinToDb(float lin) => lin <= 0f ? float.NegativeInfinity : 20f * MathF.Log10(lin);

    public static int MsToSamples(float ms, double fs) => (int)Math.Round(ms * 0.001 * fs);
}
