namespace SampleDrum.App.Audio;

/// <summary>
/// Drives an <see cref="SampleDrum.Core.IBlockProcessor"/> from an audio source (ASIO or simulation).
/// Start/Stop/Dispose are called from the UI thread.
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

    /// <summary>Opens and starts the audio. Throws <see cref="AudioHostException"/> with a pt-BR message on failure.</summary>
    void Start();

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
