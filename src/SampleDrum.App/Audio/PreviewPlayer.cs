using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SampleDrum.App.Audio;

/// <summary>
/// Stereo preview of the simulation on the PC's default output (headphones/speakers) through NAudio 3.3.0
/// <see cref="WaveOut"/> (WinMM, event-driven, own playback thread; called WaveOutEvent before NAudio 3).
/// NEVER used in the ASIO path. The simulation thread writes into a lock-free single-producer/single-consumer ring;
/// WaveOut's thread reads from it. Underruns are filled with silence and counted; a full ring drops the new block and
/// counts an overrun — the engine thread never waits on the sound card.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private readonly int _sampleRate;
    private readonly float[] _ring;      // interleaved stereo
    private readonly int _mask;
    private long _writePos;              // in floats, written by the producer only
    private long _readPos;               // in floats, written by the consumer only
    private readonly int _prebufferFloats;
    private volatile bool _primed;
    private long _underruns, _overruns;
    private WaveOut? _waveOut;

    /// <param name="sampleRate">Rate of the preview signal (the engine rate).</param>
    /// <param name="bufferMs">WaveOut buffer length (latency = buffers × bufferMs + prebuffer).</param>
    /// <param name="buffers">Number of WaveOut buffers.</param>
    public PreviewPlayer(int sampleRate, int bufferMs = 40, int buffers = 3)
    {
        _sampleRate = sampleRate;
        int cap = 1;
        while (cap < sampleRate * 2) cap <<= 1;            // >= 1 s stereo
        _ring = new float[cap];
        _mask = cap - 1;
        _prebufferFloats = sampleRate * 2 * 80 / 1000;    // start playing with 80 ms queued
        BufferMs = bufferMs;
        Buffers = buffers;
    }

    public int BufferMs { get; }
    public int Buffers { get; }
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overruns => Interlocked.Read(ref _overruns);

    /// <summary>Opens the default output (WAVE_MAPPER) and starts playing silence until data arrives.</summary>
    /// <returns>False with a pt-BR <paramref name="error"/> when no output device can be opened.</returns>
    public bool TryStart(out string? error)
    {
        error = null;
        if (_waveOut is not null) return true;
        try
        {
            var wo = new WaveOut { DeviceNumber = -1, BufferMilliseconds = BufferMs, NumberOfBuffers = Buffers };
            wo.Init(new RingProvider(this));
            wo.Play();
            _waveOut = wo;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Não foi possível abrir a saída de áudio padrão do PC para a prévia: {ex.Message}";
            return false;
        }
    }

    /// <summary>Producer (simulation thread): no allocation, no lock. Drops the block when the ring is full.</summary>
    public void Write(ReadOnlySpan<float> stereoInterleaved)
    {
        long w = _writePos, r = Volatile.Read(ref _readPos);
        int free = _ring.Length - (int)(w - r);
        if (stereoInterleaved.Length > free) { Interlocked.Increment(ref _overruns); return; }
        int start = (int)(w & _mask);
        int first = Math.Min(stereoInterleaved.Length, _ring.Length - start);
        stereoInterleaved[..first].CopyTo(_ring.AsSpan(start));
        stereoInterleaved[first..].CopyTo(_ring);
        Volatile.Write(ref _writePos, w + stereoInterleaved.Length);
    }

    /// <summary>Consumer (WaveOut thread).</summary>
    private int ReadFloats(Span<float> dest)
    {
        long r = _readPos, w = Volatile.Read(ref _writePos);
        int avail = (int)(w - r);
        if (!_primed)
        {
            if (avail < _prebufferFloats) { dest.Clear(); return dest.Length; }
            _primed = true;
        }
        int n = Math.Min(avail, dest.Length) & ~1;
        int start = (int)(r & _mask);
        int first = Math.Min(n, _ring.Length - start);
        _ring.AsSpan(start, first).CopyTo(dest);
        _ring.AsSpan(0, n - first).CopyTo(dest[first..]);
        Volatile.Write(ref _readPos, r + n);
        if (n < dest.Length)
        {
            dest[n..].Clear();
            Interlocked.Increment(ref _underruns);
            _primed = false; // re-prebuffer after an underrun so playback does not stutter block by block
        }
        return dest.Length;
    }

    public void Stop()
    {
        var wo = _waveOut;
        _waveOut = null;
        if (wo is null) return;
        try { wo.Stop(); } catch { /* best effort */ }
        try { wo.Dispose(); } catch { /* best effort */ }
    }

    public void Dispose() => Stop();

    /// <summary>IEEE float stereo provider reading the ring (NAudio 3: IWaveProvider.Read(Span&lt;byte&gt;)).</summary>
    private sealed class RingProvider(PreviewPlayer owner) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(owner._sampleRate, 2);

        public int Read(Span<byte> buffer)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buffer[..(buffer.Length & ~3)]);
            owner.ReadFloats(floats);
            return buffer.Length & ~3; // always "full": silence on underrun keeps WaveOut playing
        }
    }
}
