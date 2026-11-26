namespace SampleDrum.Core;

/// <summary>Lock-free single-producer/single-consumer ring buffer (producer = audio thread). No allocation after construction.</summary>
internal sealed class EventRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _write;   // producer
    private long _read;    // consumer
    private long _dropped;

    public EventRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPowerOfTwo));
        _items = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Producer side; drops (and counts) the item when the ring is full, never blocks.</summary>
    public void Enqueue(in T item)
    {
        long w = _write;
        if (w - Volatile.Read(ref _read) >= _items.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _items[w & _mask] = item;
        Volatile.Write(ref _write, w + 1);
    }

    /// <summary>Consumer side.</summary>
    public bool TryDequeue(out T item)
    {
        long r = _read;
        if (r == Volatile.Read(ref _write)) { item = default; return false; }
        item = _items[r & _mask];
        Volatile.Write(ref _read, r + 1);
        return true;
    }
}

/// <summary>Receives the events of one <see cref="TriggerPipeline"/> (audio thread; must not allocate).</summary>
internal abstract class TriggerEventSink
{
    public abstract void Emit(in TriggerEvent e);
}

/// <summary>
/// One trigger channel: detector + voice pool + manual test + input meter. Shared by <see cref="TriggerEngine"/>
/// (one pipeline) and <see cref="Kit.KitEngine"/> (one per piece; pipelines share no mutable state).
/// <see cref="ProcessBlock"/> runs on the audio thread and never allocates.
/// </summary>
internal sealed class TriggerPipeline
{
    private readonly OnsetDetector _detector;
    private readonly VoicePool _voices = new();
    private EngineParams _params;     // audio thread (or while stopped)
    private bool _wasEnabled = true;  // audio thread
    private int _testRequested;       // 0/1, any thread -> audio thread
    private int _testGainBits;
    private int _inputPeakBits;       // float bits of max |x| since the last read

    public TriggerPipeline(EngineParams p)
    {
        _params = p;
        _detector = new OnsetDetector(p);
    }

    public EngineParams Params => _params;

    public int ActiveVoiceCount => _voices.ActiveCount;

    internal OnsetDetector Detector => _detector;

    /// <summary>Applies new parameters (audio thread at block start, or while stopped).</summary>
    public void SetParams(EngineParams p)
    {
        _params = p;
        _detector.SetParams(p);
    }

    /// <summary>Any thread: plays the sample at the start of the next block, even when disabled.</summary>
    public void RequestTest(float gain)
    {
        Volatile.Write(ref _testGainBits, BitConverter.SingleToInt32Bits(gain));
        Volatile.Write(ref _testRequested, 1);
    }

    public float ReadAndResetInputPeak()
        => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _inputPeakBits, 0));

    /// <summary>Clears voices, detector state, pending test and meter (only while no audio thread runs).</summary>
    public void Reset(bool enabled)
    {
        _detector.Reset();
        _voices.Clear();
        Volatile.Write(ref _testRequested, 0);
        Interlocked.Exchange(ref _inputPeakBits, 0);
        _wasEnabled = enabled;
    }

    /// <summary>
    /// Processes one block. <paramref name="output"/> is added into. An automatic trigger firing at sample i of the
    /// block starts its voice at offset i (sample accurate); a manual test starts at offset 0.
    /// </summary>
    public void ProcessBlock(ReadOnlySpan<float> input, Span<float> output, long blockStart, float[]? sample,
        bool enabled, TriggerEventSink sink)
    {
        var p = _params;
        if (!enabled && _wasEnabled) _voices.FadeOutAutomatic(p.StealFadeSamples);
        _wasEnabled = enabled;

        int n = input.Length;
        int outLen = Math.Min(n, output.Length);

        if (Interlocked.Exchange(ref _testRequested, 0) != 0)
        {
            float g = BitConverter.Int32BitsToSingle(Volatile.Read(ref _testGainBits));
            if (!float.IsFinite(g) || g < 0f) g = 0f;
            float gain = g * p.OutputGainLin;
            if (sample != null) _voices.Start(sample, gain, isTest: true, p.MaxVoices, p.StealFadeSamples);
            sink.Emit(new TriggerEvent(blockStart, 0f, g, true) { PlaybackSampleIndex = blockStart, Gain = gain });
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
                sink.Emit(new TriggerEvent(onset.OnsetIndex, onset.Peak, onset.Velocity, false)
                {
                    PlaybackSampleIndex = onset.FireIndex,
                    Gain = gain,
                    IsRetrigger = onset.IsRetrigger,
                });
            }
        }
        _voices.Render(output.Slice(segStart, outLen - segStart));
        UpdatePeak(peak);
    }

    private void UpdatePeak(float peak)
    {
        // Non-negative floats order like their int bit patterns, so an int max works. NaN input is ignored.
        if (!(peak >= 0f)) return;
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
