namespace SampleDrum.Core;

/// <summary>
/// Onset detector + sample player. One instance per trigger channel.
/// Thread model: <see cref="Process"/> runs on the audio thread; every other member is safe to call
/// from other threads (lock-free).
/// </summary>
public sealed class TriggerEngine : IBlockProcessor
{
    private volatile bool _enabled = true;

    public TriggerEngine(int sampleRate, TriggerSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        Settings = settings.Normalized();
    }

    public int SampleRate { get; }

    /// <summary>Most recently submitted (normalized) settings.</summary>
    public TriggerSettings Settings { get; private set; }

    /// <summary>Automatic triggering on/off. Disabled => silence, except manual test.</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        // STUB (contract only) - implementation follows.
    }

    /// <summary>Thread-safe; plays the sample on the next block even when disabled.</summary>
    public void RequestTest(float gain = 1f) { }

    /// <summary>Called off the audio thread; atomic reference swap. Samples must already be at <see cref="SampleRate"/>.</summary>
    public void SetSample(float[] monoSamplesAtEngineRate) { }

    /// <summary>Off the audio thread; atomic swap, applied at the start of the next block.</summary>
    public void UpdateSettings(TriggerSettings s) { Settings = s.Normalized(); }

    /// <summary>UI/logging thread (single consumer). Lock-free SPSC ring buffer.</summary>
    public bool TryDequeueEvent(out TriggerEvent e) { e = default; return false; }

    /// <summary>UI meter polling: linear peak of the input since the previous call.</summary>
    public float ReadAndResetInputPeak() => 0f;
}
