namespace SampleDrum.Core;

/// <summary>
/// Block-based audio processor. The same implementation is used live (ASIO callback)
/// and offline (WAV files), so both paths share exactly the same code.
/// </summary>
public interface IBlockProcessor
{
    /// <summary>
    /// Audio thread. Must not allocate, lock, log, block or do I/O.
    /// </summary>
    /// <param name="input">Mono float samples (-1..1) of the trigger input channel for this block.</param>
    /// <param name="output">Mono float buffer for the return channel, pre-cleared to 0 by the caller;
    /// the processor adds into it. Must have the same length as <paramref name="input"/>.</param>
    void Process(ReadOnlySpan<float> input, Span<float> output);
}
