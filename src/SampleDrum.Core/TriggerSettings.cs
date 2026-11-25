namespace SampleDrum.Core;

/// <summary>
/// Immutable detector + player settings. Swap a whole instance with
/// <see cref="TriggerEngine.UpdateSettings"/> (use <c>with</c> expressions to change one field).
/// All levels are dBFS on the detector path (after the optional high-pass filter).
/// </summary>
public sealed record TriggerSettings
{
    /// <summary>Trigger threshold (dBFS). The envelope crossing this level (while armed) is an onset.</summary>
    public float ThresholdDb { get; init; } = -30f;

    /// <summary>
    /// Re-arm (release) threshold (dBFS), must be &lt;= <see cref="ThresholdDb"/>. After a trigger the detector
    /// stays disarmed until the envelope stays below this level for <see cref="RearmTimeMs"/>.
    /// </summary>
    public float RearmDb { get; init; } = -40f;

    /// <summary>Time (ms) the envelope must stay below <see cref="RearmDb"/> before re-arming. 0 = immediately.</summary>
    public float RearmTimeMs { get; init; } = 10f;

    /// <summary>Minimum interval (ms) between two onsets (lockout). Onsets inside the lockout are suppressed.</summary>
    public float MinIntervalMs { get; init; } = 50f;

    /// <summary>
    /// Velocity scan window (ms) after the threshold crossing, used to find the hit peak.
    /// Adds exactly this much latency. 0 = trigger immediately on the crossing sample
    /// (velocity then reflects only the level at the crossing, i.e. close to the threshold).
    /// </summary>
    public float ScanMs { get; init; } = 2f;

    /// <summary>Envelope (abs peak follower) release time constant (ms). Attack is instantaneous.</summary>
    public float EnvelopeReleaseMs { get; init; } = 15f;

    /// <summary>High-pass filter cutoff (Hz) on the detector path only. 0 = off (default).</summary>
    public float HighPassHz { get; init; } = 0f;

    /// <summary>
    /// dB range above the threshold mapped to velocity 0..1:
    /// velocity = clamp((peakDb - ThresholdDb) / VelocityRangeDb, 0, 1).
    /// </summary>
    public float VelocityRangeDb { get; init; } = 24f;

    /// <summary>
    /// Dynamics amount 0..1. 0 = every hit plays at full level; 1 = the sample level follows the hit level
    /// 1:1 in dB over <see cref="VelocityRangeDb"/>. gainDb = -(1 - velocity) * Dynamics * VelocityRangeDb.
    /// </summary>
    public float Dynamics { get; init; } = 0.75f;

    /// <summary>Overall output gain (dB) applied to every voice, including manual tests.</summary>
    public float OutputGainDb { get; init; } = 0f;

    /// <summary>Maximum simultaneous voices (1..16). When exceeded the oldest voice is faded out (stolen).</summary>
    public int MaxVoices { get; init; } = 4;

    /// <summary>Fade-out time (ms) applied to a stolen voice (and to voices cut by disabling) to avoid clicks.</summary>
    public float StealFadeMs { get; init; } = 2f;

    /// <summary>Default settings.</summary>
    public static TriggerSettings Default { get; } = new();

    /// <summary>Returns a copy with every value clamped to a safe range (RearmDb &lt;= ThresholdDb, etc.).</summary>
    public TriggerSettings Normalized()
    {
        float thr = Clamp(Finite(ThresholdDb, -30f), -90f, 0f);
        return this with
        {
            ThresholdDb = thr,
            RearmDb = Clamp(Finite(RearmDb, thr - 10f), -120f, thr),
            RearmTimeMs = Clamp(Finite(RearmTimeMs, 10f), 0f, 1000f),
            MinIntervalMs = Clamp(Finite(MinIntervalMs, 50f), 0f, 5000f),
            ScanMs = Clamp(Finite(ScanMs, 2f), 0f, 50f),
            EnvelopeReleaseMs = Clamp(Finite(EnvelopeReleaseMs, 15f), 0.1f, 2000f),
            HighPassHz = Clamp(Finite(HighPassHz, 0f), 0f, 2000f),
            VelocityRangeDb = Clamp(Finite(VelocityRangeDb, 24f), 1f, 120f),
            Dynamics = Clamp(Finite(Dynamics, 0.75f), 0f, 1f),
            OutputGainDb = Clamp(Finite(OutputGainDb, 0f), -60f, 24f),
            MaxVoices = Math.Clamp(MaxVoices, 1, 16),
            StealFadeMs = Clamp(Finite(StealFadeMs, 2f), 0f, 50f),
        };
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    private static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;
}
