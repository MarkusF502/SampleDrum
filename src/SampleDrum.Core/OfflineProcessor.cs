namespace SampleDrum.Core;

/// <summary>Result of an offline run.</summary>
public sealed record OfflineResult(float[] Output, IReadOnlyList<TriggerEvent> Events, long DroppedEvents);

/// <summary>
/// Runs a whole signal through <see cref="TriggerEngine"/> in fixed-size blocks, exactly as the live
/// audio callback does (same engine, same Process call, output pre-cleared per block).
/// </summary>
public static class OfflineProcessor
{
    public static OfflineResult Run(ReadOnlySpan<float> mic, float[]? sample, int sampleRate,
        TriggerSettings settings, int blockSize, bool enabled = true)
    {
        var engine = new TriggerEngine(sampleRate, settings) { Enabled = enabled };
        if (sample != null) engine.SetSample(sample);
        return Run(engine, mic, blockSize);
    }

    /// <summary>Processes <paramref name="mic"/> with an existing engine. A final partial block is processed as is.</summary>
    public static OfflineResult Run(TriggerEngine engine, ReadOnlySpan<float> mic, int blockSize)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        var output = new float[mic.Length];
        var events = new List<TriggerEvent>();
        var block = new float[blockSize];
        for (int start = 0; start < mic.Length; start += blockSize)
        {
            int n = Math.Min(blockSize, mic.Length - start);
            var outBlock = block.AsSpan(0, n);
            outBlock.Clear();
            engine.Process(mic.Slice(start, n), outBlock);
            outBlock.CopyTo(output.AsSpan(start, n));
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
        }
        return new OfflineResult(output, events, engine.DroppedEvents);
    }
}
