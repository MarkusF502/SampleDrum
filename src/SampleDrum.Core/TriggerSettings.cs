namespace SampleDrum.Core;

/// <summary>
/// Immutable detector + player settings. Swap a whole instance with
/// <see cref="TriggerEngine.UpdateSettings"/> (use <c>with</c> expressions to change one field).
/// All levels are dBFS on the detector path (after the optional high/low-pass filters).
/// Defaults follow docs/research/lsp-trigger.md (kick @ 48 kHz) and must be calibrated with a real recording.
/// </summary>
public sealed record TriggerSettings
{
    /// <summary>Detect (trigger) threshold, dBFS. A sample reaching it while the detector is OFF starts DETECT.</summary>
    public float ThresholdDb { get; init; } = -24f;

    /// <summary>
    /// Debounce time (ms): after the crossing, the mean-square level over this window must reach the threshold,
    /// otherwise the crossing is treated as an isolated spike and ignored. 0 = no debounce.
    /// The trigger fires at crossing + max(DetectTimeMs, ScanMs), so this is added latency.
    /// </summary>
    public float DetectTimeMs { get; init; } = 0.5f;

    /// <summary>
    /// Release (re-arm) level, absolute dBFS, must be &lt;= <see cref="ThresholdDb"/>. Default = threshold - 6 dB.
    /// After a trigger the detector stays ON until the envelope stays below this level for
    /// <see cref="RearmTimeMs"/>; only then it is re-armed (OFF). Use <see cref="WithThreshold"/> to move the
    /// threshold while keeping the release offset.
    /// </summary>
    public float RearmDb { get; init; } = -30f;

    /// <summary>Release time (ms): time the envelope must stay below <see cref="RearmDb"/> before re-arming. 0 = immediately.</summary>
    public float RearmTimeMs { get; init; } = 15f;

    /// <summary>Absolute lockout (ms) between two triggers, measured onset to onset. Crossings inside it are ignored.</summary>
    public float MinIntervalMs { get; init; } = 35f;

    /// <summary>
    /// Re-trigger during a sustain (dB, 0 = off). After <see cref="MinIntervalMs"/>, while the detector is still
    /// ON/RELEASE, a new hit is accepted when the envelope rises this many dB above its minimum since the lockout
    /// ended (or since the last re-trigger attempt), and is above the threshold. Keeps fast doubles that land on
    /// a ringing sustain that never drops below the release level.
    /// </summary>
    public float RetriggerRiseDb { get; init; } = 9f;

    /// <summary>
    /// Extra velocity scan window (ms) after the crossing. The peak is measured over
    /// max(DetectTimeMs, ScanMs) samples and the trigger fires at the end of that window.
    /// 0 (default) = measure inside the debounce window only (lowest latency).
    /// </summary>
    public float ScanMs { get; init; } = 0f;

    /// <summary>Envelope (peak-hold follower) release time constant (ms). Attack is instantaneous.
    /// Must exceed half a period of the kick fundamental so zero crossings do not look like releases.</summary>
    public float EnvelopeReleaseMs { get; init; } = 20f;

    /// <summary>High-pass (12 dB/oct Butterworth) on the detector path only, Hz. 0 = off.</summary>
    public float HighPassHz { get; init; } = 40f;

    /// <summary>Low-pass (12 dB/oct Butterworth) on the detector path only, Hz. 0 = off (default).
    /// 150-250 Hz helps reject snare/tom bleed but adds group delay to the detected onset.</summary>
    public float LowPassHz { get; init; } = 0f;

    /// <summary>
    /// dB range above the threshold mapped linearly to velocity 0..1:
    /// velocity = clamp((peakDb - ThresholdDb) / VelocityRangeDb, 0, 1).
    /// </summary>
    public float VelocityRangeDb { get; init; } = 24f;

    /// <summary>Sample gain (dB) at velocity 0; velocity 1 plays at 0 dB, linear in dB in between.</summary>
    public float MinGainDb { get; init; } = -20f;

    /// <summary>When true every automatic hit plays at 0 dB (velocity is still measured and reported).</summary>
    public bool FixedVelocity { get; init; } = false;

    /// <summary>Overall output gain (dB) applied to every voice, including manual tests.</summary>
    public float OutputGainDb { get; init; } = 0f;

    /// <summary>Maximum simultaneous voices (1..16). When exceeded the oldest voice is faded out (stolen).</summary>
    public int MaxVoices { get; init; } = 4;

    /// <summary>Fade-out time (ms) applied to a stolen voice (and to voices cut by disabling) to avoid clicks.</summary>
    public float StealFadeMs { get; init; } = 3f;

    /// <summary>Default settings.</summary>
    public static TriggerSettings Default { get; } = new();

    /// <summary>Release level relative to the threshold (dB, &lt;= 0).</summary>
    public float ReleaseRelativeDb => RearmDb - ThresholdDb;

    /// <summary>Returns a copy with a new threshold, moving <see cref="RearmDb"/> by the same amount (keeps the hysteresis).</summary>
    public TriggerSettings WithThreshold(float thresholdDb)
        => this with { ThresholdDb = thresholdDb, RearmDb = thresholdDb + Math.Min(0f, ReleaseRelativeDb) };

    /// <summary>Returns a copy with every value clamped to a safe range (RearmDb &lt;= ThresholdDb, etc.).</summary>
    public TriggerSettings Normalized()
    {
        float thr = Clamp(Finite(ThresholdDb, -24f), -90f, 0f);
        return this with
        {
            ThresholdDb = thr,
            DetectTimeMs = Clamp(Finite(DetectTimeMs, 0.5f), 0f, 20f),
            RearmDb = Clamp(Finite(RearmDb, thr - 6f), -120f, thr),
            RearmTimeMs = Clamp(Finite(RearmTimeMs, 15f), 0f, 1000f),
            MinIntervalMs = Clamp(Finite(MinIntervalMs, 35f), 0f, 5000f),
            RetriggerRiseDb = Clamp(Finite(RetriggerRiseDb, 9f), 0f, 48f),
            ScanMs = Clamp(Finite(ScanMs, 0f), 0f, 50f),
            EnvelopeReleaseMs = Clamp(Finite(EnvelopeReleaseMs, 20f), 0.1f, 2000f),
            HighPassHz = Clamp(Finite(HighPassHz, 40f), 0f, 2000f),
            LowPassHz = Clamp(Finite(LowPassHz, 0f), 0f, 20000f),
            VelocityRangeDb = Clamp(Finite(VelocityRangeDb, 24f), 1f, 120f),
            MinGainDb = Clamp(Finite(MinGainDb, -20f), -96f, 0f),
            OutputGainDb = Clamp(Finite(OutputGainDb, 0f), -60f, 24f),
            MaxVoices = Math.Clamp(MaxVoices, 1, 16),
            StealFadeMs = Clamp(Finite(StealFadeMs, 3f), 0f, 50f),
        };
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    private static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;
}
