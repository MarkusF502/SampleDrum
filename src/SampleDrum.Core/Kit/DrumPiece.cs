namespace SampleDrum.Core.Kit;

/// <summary>Kit pieces. The numeric value is the processing/array index (order of <see cref="IMultiChannelProcessor"/>).</summary>
public enum DrumPiece
{
    /// <summary>Bumbo — desk CH 21 (AES50 B21).</summary>
    Kick = 0,
    /// <summary>Caixa — desk CH 19 (AES50 B19).</summary>
    Snare = 1,
    /// <summary>Tom — desk CH 18 (AES50 B18).</summary>
    Tom = 2,
    /// <summary>Surdo — desk CH 17 (AES50 B17).</summary>
    FloorTom = 3,
}

/// <summary>Helpers for <see cref="DrumPiece"/>: count, pt-BR labels and the church desk channels.</summary>
public static class DrumPieces
{
    /// <summary>Number of pieces (= <see cref="IMultiChannelProcessor.PieceCount"/>).</summary>
    public const int Count = 4;

    /// <summary>All pieces in processing order.</summary>
    public static IReadOnlyList<DrumPiece> All { get; } = [DrumPiece.Kick, DrumPiece.Snare, DrumPiece.Tom, DrumPiece.FloorTom];

    /// <summary>pt-BR label: Bumbo, Caixa, Tom, Surdo.</summary>
    public static string Label(this DrumPiece p) => p switch
    {
        DrumPiece.Kick => "Bumbo",
        DrumPiece.Snare => "Caixa",
        DrumPiece.Tom => "Tom",
        DrumPiece.FloorTom => "Surdo",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Desk input channel of the piece's microphone in the church (CH 21/19/18/17).</summary>
    public static int DeskMicChannel(this DrumPiece p) => p switch
    {
        DrumPiece.Kick => 21,
        DrumPiece.Snare => 19,
        DrumPiece.Tom => 18,
        DrumPiece.FloorTom => 17,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Stage-box source of the microphone, e.g. "AES50 B21".</summary>
    public static string StageSource(this DrumPiece p) => $"AES50 B{p.DeskMicChannel()}";

    /// <summary>True for a defined piece value.</summary>
    public static bool IsValid(this DrumPiece p) => (uint)p < Count;
}
