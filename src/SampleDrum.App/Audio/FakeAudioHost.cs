using System.Diagnostics;
using SampleDrum.Core;

namespace SampleDrum.App.Audio;

/// <summary>
/// "Simulação (sem ASIO)": drives the same <see cref="IBlockProcessor"/> in real time from a paced background
/// thread, feeding a synthetic kick (120 BPM, varying velocity, low noise floor). The output is metered
/// and discarded. Lets the UI and engine be exercised on a PC without the X32.
/// </summary>
public sealed class FakeAudioHost : IAudioHost
{
    public const string DisplayName = "Simulação (sem ASIO)";

    private readonly IBlockProcessor _processor;
    private readonly int _sampleRate;
    private readonly int _blockSize;
    private readonly double _kickIntervalSec;

    private Thread? _thread;
    private volatile bool _running;
    private long _callbackCount;
    private long _kicksGenerated;
    private long _outputActiveSamples;
    private int _outputPeakBits; // float bits, max-tracked

    // Synthesis state (audio thread only).
    private long _sampleClock;
    private long _nextKickAt;
    private long _kickStart = -1;
    private float _kickAmp;
    private float _phase;
    private uint _rng = 0x9E3779B9u;
    private static readonly float[] KickLevels = [0.9f, 0.5f, 0.7f, 0.3f]; // ~ -1, -6, -3, -10 dBFS

    public FakeAudioHost(IBlockProcessor processor, int sampleRate = 48000, int blockSize = 256, double bpm = 120)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _sampleRate = sampleRate;
        _blockSize = blockSize;
        _kickIntervalSec = 60.0 / bpm;
    }

    public event Action<string>? Faulted;

    public string Description =>
        $"{DisplayName}: {_sampleRate} Hz, bloco {_blockSize} amostras, bumbo sintético a {60.0 / _kickIntervalSec:0} BPM";
    public bool IsRunning => _running;
    public int FramesPerBuffer => _blockSize;
    public int SampleRate => _sampleRate;
    public int InputLatencySamples => 0;
    public int OutputLatencySamples => 0;
    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public long KicksGenerated => Interlocked.Read(ref _kicksGenerated);
    /// <summary>Samples of output that were non-silent (|x| > -80 dBFS) since start.</summary>
    public long OutputActiveSamples => Interlocked.Read(ref _outputActiveSamples);

    /// <summary>Output peak (linear) since the previous call.</summary>
    public float ReadAndResetOutputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _outputPeakBits, 0));

    public void Start()
    {
        if (_running) return;
        _running = true;
        _sampleClock = 0;
        _nextKickAt = _sampleRate / 4; // first kick after 250 ms
        _kickStart = -1;
        _thread = new Thread(Run) { IsBackground = true, Name = "SampleDrum simulação", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Stops and waits until the simulation thread has really exited, so the caller may touch the processor
    /// (e.g. <see cref="TriggerEngine.Reset"/>) right after. The loop re-checks <c>_running</c> after every block,
    /// so this returns within about one block. Faulted is only posted (never blocks on the UI), so no deadlock.
    /// </summary>
    public void Stop()
    {
        _running = false;
        var t = _thread;
        _thread = null;
        if (t is not null && t != Thread.CurrentThread) t.Join();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        // Buffers allocated once per run, before the loop.
        var input = new float[_blockSize];
        var output = new float[_blockSize];
        long blocksDone = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            while (_running)
            {
                long blocksDue = (long)(sw.Elapsed.TotalSeconds * _sampleRate / _blockSize);
                if (blocksDue - blocksDone > _sampleRate / _blockSize) blocksDone = blocksDue - 1; // fell >1 s behind: skip
                while (blocksDone < blocksDue && _running)
                {
                    Synthesize(input);
                    Array.Clear(output);
                    _processor.Process(input, output);
                    Meter(output);
                    Interlocked.Increment(ref _callbackCount);
                    blocksDone++;
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

    private void Synthesize(Span<float> buf)
    {
        long kickLen = (long)(_sampleRate * 0.35);
        for (int i = 0; i < buf.Length; i++, _sampleClock++)
        {
            if (_sampleClock >= _nextKickAt)
            {
                _kickStart = _sampleClock;
                _kickAmp = KickLevels[(int)(_kicksGenerated % KickLevels.Length)];
                _phase = 0f;
                _nextKickAt += (long)(_kickIntervalSec * _sampleRate);
                Interlocked.Increment(ref _kicksGenerated);
            }

            // Noise floor around -60 dBFS (xorshift32, no allocation).
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            float noise = ((_rng & 0xFFFF) / 32768f - 1f) * 0.001f;

            float s = 0f;
            if (_kickStart >= 0)
            {
                long k = _sampleClock - _kickStart;
                if (k < kickLen)
                {
                    float t = k / (float)_sampleRate;
                    float freq = 50f + 110f * MathF.Exp(-t / 0.03f);      // pitch sweep 160 -> 50 Hz
                    _phase += 2f * MathF.PI * freq / _sampleRate;
                    float body = MathF.Sin(_phase) * MathF.Exp(-t / 0.12f);
                    float click = k < 48 ? (1f - k / 48f) * 0.5f : 0f;  // ~1 ms beater click
                    s = _kickAmp * (0.85f * body + 0.15f * click);
                }
                else _kickStart = -1;
            }
            buf[i] = s + noise;
        }
    }

    private void Meter(ReadOnlySpan<float> output)
    {
        float peak = 0f;
        long active = 0;
        for (int i = 0; i < output.Length; i++)
        {
            float a = MathF.Abs(output[i]);
            if (a > peak) peak = a;
            if (a > 1e-4f) active++;
        }
        if (active > 0) Interlocked.Add(ref _outputActiveSamples, active);
        int bits = BitConverter.SingleToInt32Bits(peak);
        int cur;
        while (peak > BitConverter.Int32BitsToSingle(cur = Volatile.Read(ref _outputPeakBits)))
            if (Interlocked.CompareExchange(ref _outputPeakBits, bits, cur) == cur) break;
    }
}
