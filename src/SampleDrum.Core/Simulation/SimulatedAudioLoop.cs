using System.Diagnostics;
using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>
/// Cross-platform stand-in for an audio host (tests, E2E against the panel server): a paced background thread that
/// processes the four mic tracks through <see cref="KitEngine"/> block after block, looping, like the simulation host.
/// It captures the returns and, per block, the kit the engine played (<see cref="KitEngine.PlayingKit"/>) and its
/// generation, all into preallocated arrays (no allocation, lock or I/O in the loop). It never touches the engine's
/// event queue or meters (single consumer: the caller's sampler drains them).
/// </summary>
public sealed class SimulatedAudioLoop : IDisposable
{
    private readonly KitEngine _engine;
    private readonly float[][] _mics;
    private readonly int _block;
    private readonly double _speed;
    private readonly int _len;
    private readonly float[][] _capture;
    private readonly PreparedKit?[] _kits;
    private readonly long[] _gens;
    private Thread? _thread;
    private volatile bool _running;
    private long _callbacks;
    private long _position;
    private int _capturedFrames;
    private int _recordedBlocks;

    /// <param name="engine">Engine to drive (all four pieces routed straight: mic p → piece p → return p).</param>
    /// <param name="mics">Four mic tracks (index = DrumPiece), looped.</param>
    /// <param name="blockSize">Frames per block.</param>
    /// <param name="speed">Pacing factor (1 = real time).</param>
    /// <param name="captureSeconds">Seconds of returns and per-block kits to keep from the start.</param>
    public SimulatedAudioLoop(KitEngine engine, float[][] mics, int blockSize = 256, double speed = 1.0, double captureSeconds = 20)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (mics is null || mics.Length != DrumPieces.Count) throw new ArgumentException("São necessários 4 microfones.", nameof(mics));
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        _mics = mics;
        _block = blockSize;
        _speed = speed > 0 ? speed : 1.0;
        _len = mics.Max(m => m.Length);
        int cap = (int)(captureSeconds * engine.SampleRate);
        _capture = Enumerable.Range(0, DrumPieces.Count).Select(_ => new float[cap]).ToArray();
        _kits = new PreparedKit?[cap / blockSize + 2];
        _gens = new long[_kits.Length];
    }

    public bool IsRunning => _running;
    public int BlockSize => _block;
    /// <summary>Blocks processed since Start (keeps growing; the capture stops when full).</summary>
    public long Callbacks => Interlocked.Read(ref _callbacks);
    /// <summary>Frames processed since Start.</summary>
    public long Position => Interlocked.Read(ref _position);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "SampleDrum laço de áudio simulado", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Stops and returns once the loop thread has exited.</summary>
    public void Stop()
    {
        _running = false;
        var t = _thread;
        _thread = null;
        if (t is not null && t != Thread.CurrentThread) t.Join();
    }

    public void Dispose() => Stop();

    /// <summary>Waits until at least <paramref name="frames"/> were processed (or the timeout elapsed).</summary>
    public bool WaitForPosition(long frames, TimeSpan timeout)
    {
        var limit = DateTime.UtcNow + timeout;
        while (Position < frames)
        {
            if (DateTime.UtcNow > limit || !_running) return Position >= frames;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Captured returns (index = DrumPiece) and per-block kit record. Call after <see cref="Stop"/>.</summary>
    public (float[][] ByPiece, PreparedKit?[] Kits, long[] Generations) GetCapture()
    {
        if (_running) throw new InvalidOperationException("Pare o laço antes de ler a captura.");
        int frames = Volatile.Read(ref _capturedFrames);
        int blocks = Volatile.Read(ref _recordedBlocks);
        return (_capture.Select(c => c[..frames]).ToArray(), _kits[..blocks], _gens[..blocks]);
    }

    private void Run()
    {
        int n4 = DrumPieces.Count;
        var inputs = Enumerable.Range(0, n4).Select(_ => new float[_block]).ToArray();
        var outputs = Enumerable.Range(0, n4).Select(_ => new float[_block]).ToArray();
        long pos = 0, done = 0;
        var sw = Stopwatch.StartNew();
        while (_running)
        {
            long due = (long)(sw.Elapsed.TotalSeconds * _speed * _engine.SampleRate / _block);
            if (due - done > _engine.SampleRate / _block) done = due - 1;
            while (done < due && _running)
            {
                for (int p = 0; p < n4; p++)
                {
                    var src = _mics[p];
                    var dst = inputs[p];
                    for (int k = 0; k < _block; k++)
                    {
                        long i = (pos + k) % _len;
                        dst[k] = i < src.Length ? src[i] : 0f;
                    }
                    Array.Clear(outputs[p]);
                }
                _engine.Process(inputs, outputs, _block);

                int cap = _capturedFrames;
                int capN = Math.Min(_block, _capture[0].Length - cap);
                if (capN > 0)
                {
                    for (int p = 0; p < n4; p++) Array.Copy(outputs[p], 0, _capture[p], cap, capN);
                    Volatile.Write(ref _capturedFrames, cap + capN);
                    int b = _recordedBlocks;
                    if (b < _kits.Length)
                    {
                        _kits[b] = _engine.PlayingKit;
                        _gens[b] = _engine.AppliedKitGeneration;
                        Volatile.Write(ref _recordedBlocks, b + 1);
                    }
                }
                pos += _block;
                done++;
                Interlocked.Exchange(ref _position, pos);
                Interlocked.Increment(ref _callbacks);
            }
            Thread.Sleep(1);
        }
    }
}
