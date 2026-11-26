namespace SampleDrum.Core.Kit;

/// <summary>
/// Multi-channel block processor: one mono input and one mono return per <see cref="DrumPiece"/>.
/// The same implementation runs live (ASIO), in simulation and offline.
/// </summary>
public interface IMultiChannelProcessor
{
    /// <summary>Number of pieces (4); array index = (int)<see cref="DrumPiece"/>.</summary>
    int PieceCount { get; }

    /// <summary>
    /// Audio thread: no allocation, lock, log, blocking or I/O.
    /// <paramref name="inputs"/>[p] = mono input of piece p (length &gt;= <paramref name="frames"/>); the host passes
    /// silence when the piece has no routed input. <paramref name="outputs"/>[p] = mono return of piece p,
    /// pre-cleared by the host; the processor adds into it; the host discards it when the piece has no routed output.
    /// </summary>
    void Process(float[][] inputs, float[][] outputs, int frames);
}

/// <summary>A trigger (automatic or manual test) of one piece.</summary>
public readonly record struct KitEvent(DrumPiece Piece, TriggerEvent Event);
