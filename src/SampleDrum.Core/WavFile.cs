namespace SampleDrum.Core;

/// <summary>Sample encodings supported by <see cref="WavFile.WriteMono"/>.</summary>
public enum WavSampleFormat { Pcm16, Pcm24, Float32 }

/// <summary>Decoded WAV contents, already downmixed to mono.</summary>
/// <param name="Mono">Mono samples (average of all channels), at <paramref name="SampleRate"/>.</param>
public sealed record WavData(float[] Mono, int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>Dependency-free WAV reader/writer (PCM16/24/32, float32; mono/stereo -> mono).</summary>
public static class WavFile
{
    public static WavData Read(string path) => throw new NotImplementedException();
    public static WavData Read(Stream stream) => throw new NotImplementedException();

    /// <summary>
    /// Reads a file, downmixes to mono and resamples offline to <paramref name="targetSampleRate"/> when needed.
    /// <paramref name="note"/> is a pt-BR message when a conversion happened, otherwise null.
    /// </summary>
    public static float[] ReadMonoAtRate(string path, int targetSampleRate, out string? note) => throw new NotImplementedException();

    /// <summary>Offline band-limited resampler (not for the audio thread).</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate) => throw new NotImplementedException();

    public static void WriteMono(string path, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Float32) => throw new NotImplementedException();
    public static void WriteMono(Stream stream, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Float32) => throw new NotImplementedException();
}
