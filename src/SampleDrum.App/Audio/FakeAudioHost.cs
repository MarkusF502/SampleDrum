using System.Diagnostics;
using SampleDrum.Core;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Audio;

/// <summary>
/// "Simulação (sem ASIO)": drives the SAME multi-channel processor (<see cref="KitEngine"/>) as the ASIO host, in real
/// time, from four synchronized mono sources (synthetic kit scenario, four mic WAVs or one multichannel WAV), with the
/// same block sizes as ASIO. Routing is honoured exactly like <see cref="AsioDuplexHost"/>: a piece without a routed
/// input gets silence, the return of a piece without a routed output is discarded. Returns can be captured (harness)
/// and/or mixed to a stereo preview on the PC's default output (<see cref="PreviewPlayer"/>, never in the ASIO path).
/// Start/Stop/Dispose from the UI (or harness) thread; the processing runs on a dedicated paced thread.
/// </summary>
public sealed class FakeAudioHost : IAudioHost
{
    public const string DisplayName = "Simulação (sem ASIO)";
    public const int EngineRate = 48000;

    private readonly IMultiChannelProcessor _processor;
    private readonly ResolvedRouting _routing;
    private readonly FakeHostOptions _options;

    private Thread? _thread;
    private volatile bool _running;
    private long _callbackCount;
    private long _position;
    private readonly int[] _outPeakBits = new int[DrumPieces.Count];
    private int _anyOutPeakBits;

    private float[]?[] _mics = new float[]?[DrumPieces.Count];
    private int _sourceLength;
    private volatile PreviewMixSettings _preview;
    private volatile bool _previewEnabled;
    private volatile PreviewPlayer? _player;

    private float[][]? _captureByPiece;
    private SortedDictionary<int, float[]>? _capturePhysical;
    private int _captureLength;
    private long _captured;

    /// <summary>v2: multi-channel host. <paramref name="routing"/> = validated piece → USB index mapping.</summary>
    public FakeAudioHost(IMultiChannelProcessor processor, ResolvedRouting routing, FakeHostOptions? options = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _options = options ?? new FakeHostOptions();
        if (_options.BlockSize <= 0 || _options.BlockSize > 8192)
            throw new ArgumentOutOfRangeException(nameof(options), "Bloco inválido.");
        _preview = _options.Preview;
        _previewEnabled = _options.PreviewEnabled;
    }

    public event Action<string>? Faulted;

    /// <summary>Raised (on the simulation thread) when a non-looping source reached its end and the host stopped.</summary>
    public event Action? Finished;

    public string Description =>
        $"{DisplayName}: {EngineRate} Hz, bloco {_options.BlockSize} amostras ({_options.BlockSize * 1000.0 / EngineRate:0.00} ms), " +
        $"fonte {SourceText}{(_options.Loop ? ", em loop" : "")}; peças roteadas: " +
        (_routing.RoutedPieces.Count == 0 ? "nenhuma" : string.Join(", ", _routing.RoutedPieces.Select(p => p.Label())));

    private string SourceText => _options.Source switch
    {
        SimulationSource.SyntheticKit => $"kit sintético (semente {_options.Seed})",
        SimulationSource.MicWavs => "4 WAVs de microfone",
        _ => $"WAV multicanal {Path.GetFileName(_options.MultichannelPath)}",
    };

    public bool IsRunning => _running;
    public int FramesPerBuffer => _options.BlockSize;
    public int SampleRate => EngineRate;
    public int InputLatencySamples => 0;
    public int OutputLatencySamples => 0;
    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public IMultiChannelProcessor? Processor => _processor;
    public ResolvedRouting Routing => _routing;
    public FakeHostOptions Options => _options;

    /// <summary>The generated scenario when the source is <see cref="SimulationSource.SyntheticKit"/> (after Start).</summary>
    public KitScenario? Scenario { get; private set; }

    /// <summary>pt-BR notes from loading the sources (resampling, mono downmix, unrouted pieces...).</summary>
    public IReadOnlyList<string> LoadNotes { get; private set; } = [];

    /// <summary>Frames processed since start (position in the source before wrapping).</summary>
    public long PositionSamples => Interlocked.Read(ref _position);
    public int SourceLengthSamples => _sourceLength;

    /// <summary>Preview on the PC's default output. Can be toggled while running (call from the UI thread).</summary>
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            _previewEnabled = value;
            if (value && _running) EnsurePlayer();
            if (!value) StopPlayer();
        }
    }

    /// <summary>Preview mix (volume, pieces, mic level); swap the whole record while running.</summary>
    public PreviewMixSettings Preview { get => _preview; set => _preview = value ?? PreviewMixSettings.Default; }

    /// <summary>pt-BR error of the last attempt to open the preview output, or null.</summary>
    public string? PreviewError { get; private set; }
    public long PreviewUnderruns => _player?.Underruns ?? 0;
    public long PreviewOverruns => _player?.Overruns ?? 0;

    /// <summary>Peak (linear) of a piece's routed return since the previous call.</summary>
    public float ReadAndResetOutputPeak(DrumPiece p) =>
        BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _outPeakBits[(int)p], 0));

    /// <summary>v1 compatibility: peak of all routed returns since the previous call.</summary>
    public float ReadAndResetOutputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _anyOutPeakBits, 0));

    public void Start()
    {
        if (_running) return;
        LoadSources();
        int capture = (int)Math.Min(int.MaxValue / 2, _options.CaptureSeconds * EngineRate);
        if (capture > 0)
        {
            _captureLength = capture;
            _captureByPiece = Enumerable.Range(0, DrumPieces.Count).Select(_ => new float[capture]).ToArray();
            _capturePhysical = new SortedDictionary<int, float[]>();
            foreach (var p in DrumPieces.All)
                if (_routing.OutputIndex(p) is int oi && !_capturePhysical.ContainsKey(oi + 1)) _capturePhysical[oi + 1] = new float[capture];
        }
        Interlocked.Exchange(ref _callbackCount, 0);
        Interlocked.Exchange(ref _position, 0);
        Interlocked.Exchange(ref _captured, 0);
        _running = true;
        if (_previewEnabled) EnsurePlayer();
        _thread = new Thread(Run) { IsBackground = true, Name = "SampleDrum simulação", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Stops and waits until the simulation thread has really exited, so the caller may touch the processor
    /// (e.g. <see cref="KitEngine.Reset"/>) right after. The loop re-checks the flag after every block, so this returns
    /// within about one block. Faulted/Finished are raised on the simulation thread and never block on the UI.
    /// </summary>
    public void Stop()
    {
        _running = false;
        var t = _thread;
        _thread = null;
        if (t is not null && t != Thread.CurrentThread) t.Join();
        StopPlayer();
    }

    public void Dispose() => Stop();

    /// <summary>Waits until the simulation thread exited by itself (non-looping source finished) or the timeout elapsed.</summary>
    public bool WaitForFinish(TimeSpan timeout)
    {
        var t = _thread;
        return t is null || t.Join(timeout);
    }

    /// <summary>Captured routed returns (once stopped): by piece and by desk output channel. Null without capture.</summary>
    public (float[][] ByPiece, SortedDictionary<int, float[]> Physical, int Frames)? GetCapture()
    {
        if (_captureByPiece is null || _capturePhysical is null || _running) return null;
        int frames = (int)Math.Min(Interlocked.Read(ref _captured), _captureLength);
        return (_captureByPiece.Select(a => a[..frames]).ToArray(),
            new SortedDictionary<int, float[]>(_capturePhysical.ToDictionary(kv => kv.Key, kv => kv.Value[..frames])), frames);
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        var player = new PreviewPlayer(EngineRate);
        if (player.TryStart(out var err)) { _player = player; PreviewError = null; }
        else { PreviewError = err; player.Dispose(); }
    }

    private void StopPlayer()
    {
        var p = _player;
        _player = null;
        p?.Dispose();
    }

    private void LoadSources()
    {
        var notes = new List<string>();
        var mics = new float[]?[DrumPieces.Count];
        Scenario = null;
        switch (_options.Source)
        {
            case SimulationSource.SyntheticKit:
                Scenario = KitScenario.Generate(_options.Seed, EngineRate);
                for (int i = 0; i < mics.Length; i++) mics[i] = Scenario.Mics[i];
                break;
            case SimulationSource.MicWavs:
                for (int i = 0; i < mics.Length; i++)
                {
                    string? path = i < _options.MicPaths.Count ? _options.MicPaths[i] : null;
                    if (string.IsNullOrWhiteSpace(path)) { notes.Add($"{((DrumPiece)i).Label()}: sem WAV de microfone (silêncio)."); continue; }
                    mics[i] = ReadMic(path, -1, (DrumPiece)i, notes);
                }
                break;
            case SimulationSource.MultichannelWav:
                if (string.IsNullOrWhiteSpace(_options.MultichannelPath))
                    throw new AudioHostException("Escolha o WAV multicanal da simulação.");
                for (int i = 0; i < mics.Length; i++)
                {
                    int ch = _options.MultichannelChannels is { } map && i < map.Count ? map[i] : i + 1;
                    if (ch <= 0) { notes.Add($"{((DrumPiece)i).Label()}: sem canal (silêncio)."); continue; }
                    mics[i] = ReadMic(_options.MultichannelPath, ch - 1, (DrumPiece)i, notes);
                }
                break;
        }
        int len = mics.Max(m => m?.Length ?? 0);
        if (len == 0) throw new AudioHostException("A simulação não tem nenhum sinal de microfone para tocar.");
        foreach (var p in DrumPieces.All)
            if (mics[(int)p] is not null && !_routing.IsRouted(p))
                notes.Add($"{p.Label()}: tem sinal mas não está roteada neste perfil (entrada ignorada).");
        _mics = mics;
        _sourceLength = len;
        LoadNotes = notes;
    }

    private static float[] ReadMic(string path, int channel, DrumPiece p, List<string> notes)
    {
        try
        {
            var data = WavFile.ReadMonoAtRate(path, EngineRate, channel, out var note);
            if (note is not null) notes.Add($"{p.Label()}: {note}");
            return data;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            string why = ex is IOException or UnauthorizedAccessException ? UserMessages.FileError(ex, path) : ex.Message;
            throw new AudioHostException($"Não foi possível ler o microfone de {p.Label()} ({path}): {why}", ex);
        }
    }

    private void Run()
    {
        int block = _options.BlockSize, n4 = DrumPieces.Count;
        // All buffers allocated once, before the loop.
        var inputs = new float[n4][];
        var outputs = new float[n4][];
        for (int i = 0; i < n4; i++) { inputs[i] = new float[block]; outputs[i] = new float[block]; }
        var silent = new float[block];
        var routedOut = new float[n4][];
        var stereo = new float[block * 2];
        var inIdx = DrumPieces.All.Select(p => _routing.InputIndex(p)).ToArray();
        var outIdx = DrumPieces.All.Select(p => _routing.OutputIndex(p)).ToArray();
        var mics = _mics;
        int len = _sourceLength;
        bool loop = _options.Loop;
        long pos = 0, blocksDone = 0;
        double speed = _options.Speed > 0 ? _options.Speed : 1.0;
        var sw = Stopwatch.StartNew();
        try
        {
            while (_running)
            {
                long blocksDue = (long)(sw.Elapsed.TotalSeconds * speed * EngineRate / block);
                if (blocksDue - blocksDone > EngineRate / block) blocksDone = blocksDue - 1; // >1 s behind: skip ahead
                while (blocksDone < blocksDue && _running)
                {
                    for (int i = 0; i < n4; i++)
                    {
                        FillInput(inIdx[i] is not null ? mics[i] : null, pos, inputs[i], loop, len);
                        Array.Clear(outputs[i]);
                    }

                    _processor.Process(inputs, outputs, block);

                    for (int i = 0; i < n4; i++) routedOut[i] = outIdx[i] is not null ? outputs[i] : silent;
                    MeterAndCapture(outputs, outIdx, block);
                    if (_previewEnabled && _player is { } player)
                    {
                        var pv = _preview;
                        PreviewMix.MixInterleaved(routedOut, pv.MicLevelDb is null ? null : inputs, block, stereo, pv);
                        player.Write(stereo);
                    }

                    pos += block;
                    Interlocked.Exchange(ref _position, pos);
                    Interlocked.Increment(ref _callbackCount);
                    blocksDone++;
                    if (!loop && pos >= len)
                    {
                        _running = false;
                        Finished?.Invoke();
                        return;
                    }
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Faulted?.Invoke($"A simulação parou por erro no processamento: {ex.Message}");
        }
    }

    /// <summary>Source frames [pos, pos+n) into <paramref name="dst"/>: wraps at <paramref name="len"/> when looping,
    /// silence past the end otherwise; tracks shorter than the longest one are padded with silence.</summary>
    private static void FillInput(float[]? src, long pos, float[] dst, bool loop, int len)
    {
        if (src is null) { Array.Clear(dst); return; }
        for (int k = 0; k < dst.Length; k++)
        {
            long idx = pos + k;
            if (idx >= len)
            {
                if (!loop) { dst[k] = 0f; continue; }
                idx %= len;
            }
            dst[k] = idx < src.Length ? src[idx] : 0f;
        }
    }

    private void MeterAndCapture(float[][] outputs, int?[] outIdx, int block)
    {
        long cap = Interlocked.Read(ref _captured);
        bool capturing = _captureByPiece is not null && cap < _captureLength;
        int capN = capturing ? (int)Math.Min(block, _captureLength - cap) : 0;
        float any = 0f;
        for (int i = 0; i < outputs.Length; i++)
        {
            if (outIdx[i] is not int oi) continue; // not routed: discarded
            var o = outputs[i];
            float peak = 0f;
            for (int k = 0; k < block; k++) { float a = MathF.Abs(o[k]); if (a > peak) peak = a; }
            MaxInto(ref _outPeakBits[i], peak);
            if (peak > any) any = peak;
            if (capN > 0)
            {
                Array.Copy(o, 0, _captureByPiece![i], cap, capN);
                var phys = _capturePhysical![oi + 1];
                for (int k = 0; k < capN; k++) phys[cap + k] += o[k];
            }
        }
        MaxInto(ref _anyOutPeakBits, any);
        if (capN > 0) Interlocked.Add(ref _captured, capN);
    }

    private static void MaxInto(ref int bitsField, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value), cur;
        while (value > BitConverter.Int32BitsToSingle(cur = Volatile.Read(ref bitsField)))
            if (Interlocked.CompareExchange(ref bitsField, bits, cur) == cur) break;
    }
}
