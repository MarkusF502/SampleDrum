using SampleDrum.Core.Kit;

namespace SampleDrum.App.Audio;

/// <summary>
/// Drives an audio processor from an audio source (ASIO or simulation). Start/Stop/Dispose are called from the UI
/// thread.
/// <para>
/// v2 (multi-channel) shape: a host is constructed with an <see cref="IMultiChannelProcessor"/> (the
/// <see cref="KitEngine"/>) and a validated <see cref="ResolvedRouting"/> (piece → ASIO input index / output index,
/// or not routed), e.g. <c>new AsioDuplexHost(driverName, routing, kitEngine)</c> and
/// <c>new FakeAudioHost(kitEngine, routing, ...)</c>. Per block the host passes one input array and one pre-cleared
/// output array per piece (<see cref="DrumPieces.Count"/>): silence for pieces without a routed input, and it
/// discards the returns of pieces without a routed output. The host never changes the driver's rate or buffer size.
/// </para>
/// </summary>
public interface IAudioHost : IDisposable
{
    /// <summary>Short pt-BR description for the log/status bar.</summary>
    string Description { get; }

    bool IsRunning { get; }

    /// <summary>Frames per block once started (0 before).</summary>
    int FramesPerBuffer { get; }

    /// <summary>Sample rate the host runs at (Hz), valid once started.</summary>
    int SampleRate { get; }

    /// <summary>Input / output latency reported by the driver, in samples (0 when unknown / simulation).</summary>
    int InputLatencySamples { get; }
    int OutputLatencySamples { get; }

    /// <summary>Number of audio callbacks processed since start (for status display).</summary>
    long CallbackCount { get; }

    /// <summary>v2: the multi-channel processor driven by this host (null for a v1 single-channel host).</summary>
    IMultiChannelProcessor? Processor => null;

    /// <summary>v2: piece → ASIO channel routing actually opened by this host (<see cref="ResolvedRouting.None"/> for v1 hosts).</summary>
    ResolvedRouting Routing => ResolvedRouting.None;

    /// <summary>Opens and starts the audio. Throws <see cref="AudioHostException"/> with a pt-BR message on failure.</summary>
    void Start();

    /// <summary>Stops and returns only when no audio callback can run anymore (so the processor may be reset).</summary>
    void Stop();

    /// <summary>
    /// Raised when audio stops by itself (driver fault, driver reset request). pt-BR message.
    /// May be raised on a non-UI thread; handlers must marshal.
    /// </summary>
    event Action<string>? Faulted;
}

/// <summary>Start/stop failure with a user-facing pt-BR message.</summary>
public sealed class AudioHostException : Exception
{
    public AudioHostException(string message, Exception? inner = null) : base(message, inner) { }
}
