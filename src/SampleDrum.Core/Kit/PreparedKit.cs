namespace SampleDrum.Core.Kit;

/// <summary>
/// One piece of a <see cref="PreparedKit"/>: the sample already loaded, resampled to the engine rate and
/// NaN-sanitized (off the audio thread). <see cref="Samples"/> null = no sample for this piece (silent return).
/// The array is owned by the kit: never modify it after preparing.
/// </summary>
public sealed record PreparedPiece(DrumPiece Piece, float[]? Samples, string? SourcePath, string? Name)
{
    /// <summary>Optional output gain (dB) to apply with the kit; null = keep the piece's current gain.</summary>
    public float? GainDb { get; init; }

    /// <summary>Optional partial detection settings to apply with the kit; null = keep the current settings.</summary>
    public TriggerOverrides? Detection { get; init; }

    /// <summary>Empty piece (no sample).</summary>
    public static PreparedPiece Empty(DrumPiece p) => new(p, null, null, null);
}

/// <summary>
/// Immutable set of the four prepared piece samples, swapped into <see cref="KitEngine"/> with one reference
/// swap (<see cref="KitEngine.ApplyKit"/>).
/// </summary>
public sealed class PreparedKit
{
    private readonly PreparedPiece[] _pieces;

    /// <param name="name">Display name (preset name).</param>
    /// <param name="sampleRate">Rate the samples were prepared at (must match the engine).</param>
    /// <param name="pieces">Exactly one entry per piece, in any order.</param>
    public PreparedKit(string name, int sampleRate, IEnumerable<PreparedPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Name = name ?? "";
        SampleRate = sampleRate;
        _pieces = new PreparedPiece[DrumPieces.Count];
        foreach (var p in pieces)
        {
            ArgumentNullException.ThrowIfNull(p);
            if (!p.Piece.IsValid()) throw new ArgumentOutOfRangeException(nameof(pieces), p.Piece, "Peça inválida.");
            if (_pieces[(int)p.Piece] != null) throw new ArgumentException($"Peça repetida: {p.Piece}.", nameof(pieces));
            _pieces[(int)p.Piece] = p;
        }
        for (int i = 0; i < _pieces.Length; i++) _pieces[i] ??= PreparedPiece.Empty((DrumPiece)i);
    }

    public string Name { get; }
    public int SampleRate { get; }

    /// <summary>Pieces in <see cref="DrumPiece"/> order.</summary>
    public IReadOnlyList<PreparedPiece> Pieces => _pieces;

    public PreparedPiece this[DrumPiece p] => _pieces[(int)p];

    /// <summary>Copy with one piece replaced (single-piece sample change = a kit with one piece replaced).</summary>
    public PreparedKit WithPiece(PreparedPiece piece, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(piece);
        var copy = (PreparedPiece[])_pieces.Clone();
        copy[(int)piece.Piece] = piece;
        return new PreparedKit(name ?? Name, SampleRate, copy);
    }

    /// <summary>A kit without samples.</summary>
    public static PreparedKit Empty(int sampleRate, string name = "") =>
        new(name, sampleRate, DrumPieces.All.Select(PreparedPiece.Empty));
}
