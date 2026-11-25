namespace SampleDrum.Core;

/// <summary>
/// Onset detector + sample player. One instance per trigger channel; identical code path live and offline.
/// <para>
/// Thread model: <see cref="Process"/> runs on the audio thread and never allocates, locks, logs, blocks
/// or does I/O. <see cref="SetSample"/>, <see cref="UpdateSettings"/>, <see cref="RequestTest"/> and
/// <see cref="Enabled"/> may be called from any other thread (lock-free reference/flag swaps).
/// <see cref="TryDequeueEvent"/> must be called from a single consumer thread (UI/logging).
/// </para>
/// <para>
/// Timing: an automatic trigger whose fire point (onset + scan window) falls inside the block starts the
/// voice at that exact offset in the same block (sample accurate). A manual test starts at offset 0 of the
/// next processed block.
/// </para>
/// </summary>
public sealed class TriggerEngine : IBlockProcessor
{
    /// <summary>Capacity of the event ring buffer (power of two).</summary>
    public const int EventCapacity = 1024;

    private readonly OnsetDetector _detector;
    private readonly VoicePool _voices = new();
    private readonly TriggerEvent[] _events = new TriggerEvent[EventCapacity];

    private EngineParams _params;                 // audio thread only
    private EngineParams? _pendingParams;         // written by UpdateSettings, taken by Process
    private volatile float[]? _sample;
    private volatile bool _enabled = true;
    private bool _wasEnabled = true;              // audio thread only

    private int _testRequested;                   // 0/1
    private int _testGainBits;
    private int _inputPeakBits;                   // float bits of max |x| since last read
    private long _eventWrite;                     // producer: audio thread
    private long _eventRead;                      // consumer: UI thread
    private long _droppedEvents;
    private long _position;                       // absolute input sample index of the next block

    public TriggerEngine(int sampleRate, TriggerSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        _params = new EngineParams(sampleRate, settings);
        Settings = _params.Settings;
        _detector = new OnsetDetector(_params);
    }

    public int SampleRate { get; }

    /// <summary>Most recently submitted (normalized) settings.</summary>
    public TriggerSettings Settings { get; private set; }

    /// <summary>
    /// Automatic triggering on/off. When disabled the detector keeps running (so state stays consistent),
    /// but no automatic voices start and no automatic events are emitted; voices already playing are faded
    /// out over StealFadeMs. Manual tests still play.
    /// </summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>Absolute number of input samples processed so far.</summary>
    public long SamplesProcessed => Interlocked.Read(ref _position);

    /// <summary>Events lost because the consumer did not drain the ring buffer in time.</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    /// <summary>Voices currently sounding (diagnostics; read on the audio thread or when idle).</summary>
    public int ActiveVoiceCount => _voices.ActiveCount;

    /// <summary>Currently loaded sample (may be null).</summary>
    public float[]? Sample => _sample;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var pending = Interlocked.Exchange(ref _pendingParams, null);
        if (pending != null)
        {
            _params = pending;
            _detector.SetParams(pending);
        }
        var p = _params;
        var sample = _sample;
        bool enabled = _enabled;

        if (!enabled && _wasEnabled) _voices.FadeOutAutomatic(p.StealFadeSamples);
        _wasEnabled = enabled;

        int n = input.Length;
        int outLen = Math.Min(n, output.Length);
        long blockStart = _position;

        if (Interlocked.Exchange(ref _testRequested, 0) != 0)
        {
            float g = BitConverter.Int32BitsToSingle(Volatile.Read(ref _testGainBits));
            if (!float.IsFinite(g) || g < 0f) g = 0f;
            float gain = g * p.OutputGainLin;
            if (sample != null) _voices.Start(sample, gain, isTest: true, p.MaxVoices, p.StealFadeSamples);
            Enqueue(new TriggerEvent(blockStart, 0f, g, true) { PlaybackSampleIndex = blockStart, Gain = gain });
        }

        float peak = 0f;
        int segStart = 0;
        for (int i = 0; i < n; i++)
        {
            float x = input[i];
            float a = x < 0f ? -x : x;
            if (a > peak) peak = a;

            if (_detector.Step(x, blockStart + i, out var onset) && enabled)
            {
                int at = Math.Min(i, outLen);
                _voices.Render(output.Slice(segStart, at - segStart));
                segStart = at;

                float gain = p.GainFromVelocity(onset.Velocity);
                if (sample != null) _voices.Start(sample, gain, isTest: false, p.MaxVoices, p.StealFadeSamples);
                Enqueue(new TriggerEvent(onset.OnsetIndex, onset.Peak, onset.Velocity, false)
                {
                    PlaybackSampleIndex = onset.FireIndex,
                    Gain = gain,
                });
            }
        }
        _voices.Render(output.Slice(segStart, outLen - segStart));

        Volatile.Write(ref _position, blockStart + n);
        UpdatePeak(peak);
    }

    /// <summary>Thread-safe; plays the sample at the start of the next block even when disabled.</summary>
    public void RequestTest(float gain = 1f)
    {
        Volatile.Write(ref _testGainBits, BitConverter.SingleToInt32Bits(gain));
        Volatile.Write(ref _testRequested, 1);
    }

    /// <summary>
    /// Called off the audio thread; atomic reference swap. Samples must already be mono at
    /// <see cref="SampleRate"/> (see <see cref="WavFile.ReadMonoAtRate"/>). The engine keeps the reference:
    /// do not modify the array afterwards. Voices already playing finish with the previous sample.
    /// Pass an empty array to unload.
    /// </summary>
    public void SetSample(float[] monoSamplesAtEngineRate)
    {
        ArgumentNullException.ThrowIfNull(monoSamplesAtEngineRate);
        _sample = monoSamplesAtEngineRate.Length == 0 ? null : monoSamplesAtEngineRate;
    }

    /// <summary>Off the audio thread; coefficients are computed here and swapped in at the next block start.</summary>
    public void UpdateSettings(TriggerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var p = new EngineParams(SampleRate, s);
        Settings = p.Settings;
        Volatile.Write(ref _pendingParams, p);
    }

    /// <summary>UI/logging thread (single consumer). Lock-free SPSC ring buffer.</summary>
    public bool TryDequeueEvent(out TriggerEvent e)
    {
        long r = _eventRead;
        if (r == Volatile.Read(ref _eventWrite)) { e = default; return false; }
        e = _events[r & (EventCapacity - 1)];
        Volatile.Write(ref _eventRead, r + 1);
        return true;
    }

    /// <summary>UI meter polling: linear peak |x| of the input since the previous call.</summary>
    public float ReadAndResetInputPeak()
        => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _inputPeakBits, 0));

    private void Enqueue(in TriggerEvent e)
    {
        long w = _eventWrite;
        if (w - Volatile.Read(ref _eventRead) >= EventCapacity)
        {
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        _events[w & (EventCapacity - 1)] = e;
        Volatile.Write(ref _eventWrite, w + 1);
    }

    private void UpdatePeak(float peak)
    {
        // Non-negative floats order like their int bit patterns, so an int max works.
        int bits = BitConverter.SingleToInt32Bits(peak);
        int cur = Volatile.Read(ref _inputPeakBits);
        while (bits > cur)
        {
            int prev = Interlocked.CompareExchange(ref _inputPeakBits, bits, cur);
            if (prev == cur) break;
            cur = prev;
        }
    }
}
