namespace SampleDrum.Core;

/// <summary>
/// Onset detector + sample player for ONE channel (v1). One instance per trigger channel; identical code path live
/// and offline. The multi-piece engine is <see cref="Kit.KitEngine"/>, built on the same <see cref="TriggerPipeline"/>.
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

    private readonly TriggerPipeline _pipeline;
    private readonly EventRing<TriggerEvent> _events = new(EventCapacity);
    private readonly RingSink _sink;

    private EngineParams? _pendingParams;         // written by UpdateSettings, taken by Process
    private volatile float[]? _sample;
    private volatile bool _enabled = true;
    private long _position;                       // absolute input sample index of the next block

    public TriggerEngine(int sampleRate, TriggerSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        var p = new EngineParams(sampleRate, settings);
        Settings = p.Settings;
        _pipeline = new TriggerPipeline(p);
        _sink = new RingSink(_events);
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
    public long DroppedEvents => _events.Dropped;

    /// <summary>Voices currently sounding (diagnostics; read on the audio thread or when idle).</summary>
    public int ActiveVoiceCount => _pipeline.ActiveVoiceCount;

    /// <summary>Currently loaded sample (may be null).</summary>
    public float[]? Sample => _sample;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var pending = Interlocked.Exchange(ref _pendingParams, null);
        if (pending != null) _pipeline.SetParams(pending);

        long blockStart = _position;
        _pipeline.ProcessBlock(input, output, blockStart, _sample, _enabled, _sink);
        Volatile.Write(ref _position, blockStart + input.Length);
    }

    /// <summary>Thread-safe; plays the sample at the start of the next block even when disabled.</summary>
    public void RequestTest(float gain = 1f) => _pipeline.RequestTest(gain);

    /// <summary>
    /// Called off the audio thread; atomic reference swap. Samples must already be mono at
    /// <see cref="SampleRate"/> (see <see cref="WavFile.ReadMonoAtRate(string, int, out string?)"/>). The engine keeps the reference:
    /// do not modify the array afterwards. Voices already playing finish with the previous sample.
    /// Pass an empty array to unload.
    /// </summary>
    public void SetSample(float[] monoSamplesAtEngineRate)
    {
        ArgumentNullException.ThrowIfNull(monoSamplesAtEngineRate);
        // NaN/Inf would propagate to the driver output (Float32LSB passes them through): replace with silence.
        LastSampleNonFiniteCount = SampleSanitizer.ReplaceNonFinite(monoSamplesAtEngineRate);
        _sample = monoSamplesAtEngineRate.Length == 0 ? null : monoSamplesAtEngineRate;
    }

    /// <summary>Number of NaN/Inf values replaced by 0 in the last <see cref="SetSample"/> call.</summary>
    public int LastSampleNonFiniteCount { get; private set; }

    /// <summary>
    /// Clears the runtime state: voices, detector (envelope, filters, state machine, lockout), pending manual test,
    /// unapplied settings are applied now, peak meter. Keeps the sample, settings, <see cref="Enabled"/> and the
    /// absolute sample counter. NOT thread-safe with <see cref="Process"/>: call it only while no audio thread
    /// is running (e.g. before starting the audio host), so a voice cut by a previous stop never resumes.
    /// </summary>
    public void Reset()
    {
        var pending = Interlocked.Exchange(ref _pendingParams, null);
        if (pending != null) _pipeline.SetParams(pending);
        _pipeline.Reset(_enabled);
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
    public bool TryDequeueEvent(out TriggerEvent e) => _events.TryDequeue(out e);

    /// <summary>UI meter polling: linear peak |x| of the input since the previous call.</summary>
    public float ReadAndResetInputPeak() => _pipeline.ReadAndResetInputPeak();

    private sealed class RingSink(EventRing<TriggerEvent> ring) : TriggerEventSink
    {
        public override void Emit(in TriggerEvent e) => ring.Enqueue(e);
    }
}
