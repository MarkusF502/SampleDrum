namespace SampleDrum.Core;

/// <summary>
/// A detected (or manually requested) trigger.
/// </summary>
/// <param name="SampleIndex">Absolute input sample index (since engine creation) of the detected onset,
/// i.e. the first sample where the detector envelope crossed the trigger threshold.
/// For manual tests it is the first sample of the block where the test started.</param>
/// <param name="Peak">Linear peak (0..1+) measured on the detector path over the scan window.</param>
/// <param name="Velocity">Normalized velocity 0..1 derived from the peak (manual test: the requested gain).</param>
/// <param name="IsManualTest">True when produced by <see cref="TriggerEngine.RequestTest"/>.</param>
public readonly record struct TriggerEvent(long SampleIndex, float Peak, float Velocity, bool IsManualTest)
{
    /// <summary>
    /// Absolute sample index where the sample voice actually started playing
    /// (= onset when ScanMs is 0, otherwise onset + scan window).
    /// </summary>
    public long PlaybackSampleIndex { get; init; }

    /// <summary>Linear gain applied to the voice (velocity, dynamics and output gain combined).</summary>
    public float Gain { get; init; }

    /// <summary>True when accepted during the previous hit's sustain via <see cref="TriggerSettings.RetriggerRiseDb"/>.</summary>
    public bool IsRetrigger { get; init; }
}
