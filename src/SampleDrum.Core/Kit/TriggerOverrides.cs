namespace SampleDrum.Core.Kit;

/// <summary>
/// Sparse (partial) detection settings: null = keep the base value. Used by presets and by the persisted config,
/// so a value the operator never changed keeps following the Core defaults.
/// </summary>
public sealed record TriggerOverrides
{
    public float? ThresholdDb { get; init; }
    public float? DetectTimeMs { get; init; }
    public float? RearmDb { get; init; }
    public float? RearmTimeMs { get; init; }
    public float? MinIntervalMs { get; init; }
    public float? RetriggerRiseDb { get; init; }
    public float? ScanMs { get; init; }
    public float? EnvelopeReleaseMs { get; init; }
    public float? HighPassHz { get; init; }
    public float? LowPassHz { get; init; }
    public float? VelocityRangeDb { get; init; }
    public float? MinGainDb { get; init; }
    public bool? FixedVelocity { get; init; }
    public int? MaxVoices { get; init; }
    public float? StealFadeMs { get; init; }

    /// <summary>True when no field is set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => this == new TriggerOverrides();

    /// <summary>Applies the set fields on top of <paramref name="baseSettings"/> (result normalized).</summary>
    public TriggerSettings ApplyTo(TriggerSettings baseSettings) => (baseSettings with
    {
        ThresholdDb = ThresholdDb ?? baseSettings.ThresholdDb,
        DetectTimeMs = DetectTimeMs ?? baseSettings.DetectTimeMs,
        RearmDb = RearmDb ?? baseSettings.RearmDb,
        RearmTimeMs = RearmTimeMs ?? baseSettings.RearmTimeMs,
        MinIntervalMs = MinIntervalMs ?? baseSettings.MinIntervalMs,
        RetriggerRiseDb = RetriggerRiseDb ?? baseSettings.RetriggerRiseDb,
        ScanMs = ScanMs ?? baseSettings.ScanMs,
        EnvelopeReleaseMs = EnvelopeReleaseMs ?? baseSettings.EnvelopeReleaseMs,
        HighPassHz = HighPassHz ?? baseSettings.HighPassHz,
        LowPassHz = LowPassHz ?? baseSettings.LowPassHz,
        VelocityRangeDb = VelocityRangeDb ?? baseSettings.VelocityRangeDb,
        MinGainDb = MinGainDb ?? baseSettings.MinGainDb,
        FixedVelocity = FixedVelocity ?? baseSettings.FixedVelocity,
        MaxVoices = MaxVoices ?? baseSettings.MaxVoices,
        StealFadeMs = StealFadeMs ?? baseSettings.StealFadeMs,
    }).Normalized();

    /// <summary>The fields of <paramref name="s"/> that differ from <paramref name="defaults"/> (others null).</summary>
    public static TriggerOverrides Diff(TriggerSettings s, TriggerSettings defaults)
    {
        static float? F(float v, float d) => MathF.Abs(v - d) < 1e-4f ? null : v;
        return new TriggerOverrides
        {
            ThresholdDb = F(s.ThresholdDb, defaults.ThresholdDb),
            DetectTimeMs = F(s.DetectTimeMs, defaults.DetectTimeMs),
            RearmDb = F(s.RearmDb, defaults.RearmDb),
            RearmTimeMs = F(s.RearmTimeMs, defaults.RearmTimeMs),
            MinIntervalMs = F(s.MinIntervalMs, defaults.MinIntervalMs),
            RetriggerRiseDb = F(s.RetriggerRiseDb, defaults.RetriggerRiseDb),
            ScanMs = F(s.ScanMs, defaults.ScanMs),
            EnvelopeReleaseMs = F(s.EnvelopeReleaseMs, defaults.EnvelopeReleaseMs),
            HighPassHz = F(s.HighPassHz, defaults.HighPassHz),
            LowPassHz = F(s.LowPassHz, defaults.LowPassHz),
            VelocityRangeDb = F(s.VelocityRangeDb, defaults.VelocityRangeDb),
            MinGainDb = F(s.MinGainDb, defaults.MinGainDb),
            FixedVelocity = s.FixedVelocity == defaults.FixedVelocity ? null : s.FixedVelocity,
            MaxVoices = s.MaxVoices == defaults.MaxVoices ? null : s.MaxVoices,
            StealFadeMs = F(s.StealFadeMs, defaults.StealFadeMs),
        };
    }
}
