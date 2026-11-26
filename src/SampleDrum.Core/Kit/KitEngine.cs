namespace SampleDrum.Core.Kit;

/// <summary>
/// Four independent trigger pipelines (detector + voice pool + return) behind one <see cref="IMultiChannelProcessor"/>.
/// No mutable state is shared between pieces, including when several pieces trigger in the same block; the only
/// shared object is the event queue, written by the audio thread only.
/// <para>
/// Thread model: <see cref="Process"/> runs on the audio thread (no allocation/lock/log/I/O). Settings, kit, enable
/// flags and test requests are lock-free swaps/flags usable from any thread and take effect at the next block start.
/// <see cref="TryDequeueEvent"/> has a single consumer (UI/log). <see cref="Reset"/> only while no host runs.
/// </para>
/// </summary>
public sealed class KitEngine : IMultiChannelProcessor
{
    /// <summary>Capacity of the shared event queue (power of two).</summary>
    public const int EventCapacity = 2048;

    /// <summary>Chunk size used when a host passes a null input/output array (should not happen; defensive).</summary>
    private const int ScratchSize = 4096;

    private readonly TriggerPipeline[] _pipes = new TriggerPipeline[DrumPieces.Count];
    private readonly PieceSink[] _sinks = new PieceSink[DrumPieces.Count];
    private readonly EventRing<KitEvent> _events = new(EventCapacity);
    private readonly int[] _pieceEnabled = new int[DrumPieces.Count];       // 0/1, any thread
    private readonly PendingParams?[] _pendingParams = new PendingParams?[DrumPieces.Count];
    private readonly float[]?[] _samples = new float[DrumPieces.Count][];     // audio-thread view of the kit
    private readonly float[] _silence = new float[ScratchSize];
    private readonly float[] _discard = new float[ScratchSize];

    private readonly object _settingsLock = new();                          // writers only (never the audio thread)
    private readonly PieceSettings[] _settings = new PieceSettings[DrumPieces.Count];

    private KitSwap? _pendingKit;                                           // ApplyKit -> Process
    private PreparedKit? _submittedKit;
    private PreparedKit? _playingKit;                                       // audio thread
    private volatile bool _kitEnabled;
    private long _position;
    private long _seq;
    private long _kitGeneration;
    private long _appliedKitGeneration;

    public KitEngine(int sampleRate, KitSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        foreach (var p in DrumPieces.All)
        {
            int i = (int)p;
            _settings[i] = settings[p];
            _pipes[i] = new TriggerPipeline(BuildParams(settings[p]));
            _sinks[i] = new PieceSink(p, _events);
            _pieceEnabled[i] = 1;
        }
    }

    public int SampleRate { get; }
    public int PieceCount => DrumPieces.Count;

    /// <summary>Most recently submitted settings (by <see cref="UpdatePieceSettings"/> or a kit with settings).</summary>
    public KitSettings Settings
    {
        get { lock (_settingsLock) return new KitSettings(_settings); }
    }

    /// <summary>
    /// Master "Ativar/Desativar kit". Starts false. Disabled: detectors keep running but no automatic triggers or
    /// events; voices already playing fade out over StealFadeMs; manual tests still play.
    /// </summary>
    public bool KitEnabled { get => _kitEnabled; set => _kitEnabled = value; }

    /// <summary>Per-piece enable (default on). A disabled piece behaves like a disabled kit for that piece only.</summary>
    public void SetPieceEnabled(DrumPiece p, bool on) => Volatile.Write(ref _pieceEnabled[Index(p)], on ? 1 : 0);

    public bool IsPieceEnabled(DrumPiece p) => Volatile.Read(ref _pieceEnabled[Index(p)]) != 0;

    /// <summary>Manual test of one piece at the next block start (offset 0); works even when disabled.</summary>
    public void RequestTest(DrumPiece p, float gain = 1f) => _pipes[Index(p)].RequestTest(gain);

    /// <summary>Off the audio thread: coefficients are computed here and swapped in at the next block start.</summary>
    public void UpdatePieceSettings(DrumPiece p, PieceSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int i = Index(p);
        var normalized = s with { Trigger = s.Trigger.Normalized() };
        var ep = BuildParams(normalized);
        lock (_settingsLock)
        {
            _settings[i] = normalized;
            Volatile.Write(ref _pendingParams[i], new PendingParams(ep, Interlocked.Increment(ref _seq)));
        }
    }

    /// <summary>
    /// Swaps all piece samples (and the optional per-piece gain/detection of the kit) with ONE reference swap,
    /// effective at the next block start: no block ever plays a mix of two kits. Voices already playing finish with
    /// their old sample; new hits use the new kit. A single-piece change is a kit with one piece replaced
    /// (<see cref="PreparedKit.WithPiece"/>). Call off the audio thread.
    /// </summary>
    public void ApplyKit(PreparedKit kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (kit.SampleRate != SampleRate)
            throw new ArgumentException($"Kit preparado a {kit.SampleRate} Hz; o motor roda a {SampleRate} Hz.", nameof(kit));

        var parms = new EngineParams?[DrumPieces.Count];
        lock (_settingsLock)
        {
            foreach (var p in DrumPieces.All)
            {
                var piece = kit[p];
                int i = (int)p;
                var s = _settings[i];
                if (piece.Detection is { } det) s = s with { Trigger = det.ApplyTo(KitDefaults.TriggerFor(p)) };
                if (piece.GainDb is { } g) s = s with { OutputGainDb = g };
                _settings[i] = s;
                parms[i] = BuildParams(s);
            }
            Volatile.Write(ref _submittedKit, kit);
            Volatile.Write(ref _pendingKit, new KitSwap(kit, parms, Interlocked.Increment(ref _seq), ++_kitGeneration));
        }
    }

    /// <summary>The kit in effect from the next block on (last kit passed to <see cref="ApplyKit"/>), or null.</summary>
    public PreparedKit? ActiveKit => Volatile.Read(ref _submittedKit);

    /// <summary>Kit used by the last processed block (audio-thread view; diagnostics/tests).</summary>
    public PreparedKit? PlayingKit => Volatile.Read(ref _playingKit);

    /// <summary>Generation of the last kit swap taken by Process (or idle Reset); zero before the first swap.</summary>
    public long AppliedKitGeneration => Volatile.Read(ref _appliedKitGeneration);

    /// <summary>Single consumer only: the app drains this queue and fans events out to UI and remote observers.</summary>
    public bool TryDequeueEvent(out KitEvent e) => _events.TryDequeue(out e);

    /// <summary>Events lost because the consumer did not drain the queue in time.</summary>
    public long DroppedEvents => _events.Dropped;

    /// <summary>Single consumer only: the app samples this destructive meter read and fans it out to observers.</summary>
    public float ReadAndResetInputPeak(DrumPiece p) => _pipes[Index(p)].ReadAndResetInputPeak();

    /// <summary>Voices sounding on a piece (diagnostics; audio thread or idle).</summary>
    public int ActiveVoiceCount(DrumPiece p) => _pipes[Index(p)].ActiveVoiceCount;

    /// <summary>Absolute number of frames processed.</summary>
    public long SamplesProcessed => Interlocked.Read(ref _position);

    /// <summary>
    /// Clears voices, detectors, pending tests and meters of every piece; pending settings and kit are applied now.
    /// Keeps samples, settings, enables and the absolute frame counter. Only while no host runs.
    /// </summary>
    public void Reset()
    {
        ApplyPending();
        for (int i = 0; i < _pipes.Length; i++) _pipes[i].Reset(_kitEnabled && Volatile.Read(ref _pieceEnabled[i]) != 0);
    }

    public void Process(float[][] inputs, float[][] outputs, int frames)
    {
        if (frames <= 0 || inputs is null || outputs is null) return;
        ApplyPending();
        bool kitOn = _kitEnabled;
        long start = _position;

        for (int i = 0; i < _pipes.Length; i++)
        {
            float[]? inArr = i < inputs.Length ? inputs[i] : null;
            float[]? outArr = i < outputs.Length ? outputs[i] : null;
            bool enabled = kitOn && Volatile.Read(ref _pieceEnabled[i]) != 0;
            var pipe = _pipes[i];
            var sample = _samples[i];

            if (inArr is not null && outArr is not null && inArr.Length >= frames && outArr.Length >= frames)
            {
                pipe.ProcessBlock(inArr.AsSpan(0, frames), outArr.AsSpan(0, frames), start, sample, enabled, _sinks[i]);
                continue;
            }

            // Defensive path (host contract violated): missing/short input = silence, missing/short output = discarded.
            for (int off = 0; off < frames; off += ScratchSize)
            {
                int n = Math.Min(ScratchSize, frames - off);
                ReadOnlySpan<float> inSpan = inArr is not null && inArr.Length >= off + n ? inArr.AsSpan(off, n) : _silence.AsSpan(0, n);
                Span<float> outSpan;
                if (outArr is not null && outArr.Length >= off + n) outSpan = outArr.AsSpan(off, n);
                else { outSpan = _discard.AsSpan(0, n); outSpan.Clear(); }
                pipe.ProcessBlock(inSpan, outSpan, start + off, sample, enabled, _sinks[i]);
            }
        }
        Volatile.Write(ref _position, start + frames);
    }

    /// <summary>Block start (or Reset): kit swap and per-piece settings, applied in submission order.</summary>
    private void ApplyPending()
    {
        var swap = Interlocked.Exchange(ref _pendingKit, null);
        for (int i = 0; i < _pipes.Length; i++)
        {
            var pp = Interlocked.Exchange(ref _pendingParams[i], null);
            EngineParams? fromKit = swap?.Params[i];
            if (fromKit != null && pp != null)
            {
                // Both pending: the most recent submission wins.
                _pipes[i].SetParams(swap!.Seq > pp.Seq ? fromKit : pp.Params);
            }
            else if (fromKit != null) _pipes[i].SetParams(fromKit);
            else if (pp != null) _pipes[i].SetParams(pp.Params);
        }
        if (swap != null)
        {
            for (int i = 0; i < _samples.Length; i++) _samples[i] = swap.Kit[(DrumPiece)i].Samples;
            Volatile.Write(ref _playingKit, swap.Kit);
            Volatile.Write(ref _appliedKitGeneration, swap.Generation);
        }
    }

    private EngineParams BuildParams(PieceSettings s) =>
        new(SampleRate, s.Trigger with { OutputGainDb = s.Trigger.OutputGainDb + s.OutputGainDb });

    private static int Index(DrumPiece p) =>
        p.IsValid() ? (int)p : throw new ArgumentOutOfRangeException(nameof(p), p, "Peça inválida.");

    private sealed record PendingParams(EngineParams Params, long Seq);

    private sealed record KitSwap(PreparedKit Kit, EngineParams?[] Params, long Seq, long Generation);

    private sealed class PieceSink(DrumPiece piece, EventRing<KitEvent> ring) : TriggerEventSink
    {
        public override void Emit(in TriggerEvent e) => ring.Enqueue(new KitEvent(piece, e));
    }
}
