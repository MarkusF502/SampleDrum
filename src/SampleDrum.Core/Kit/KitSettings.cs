namespace SampleDrum.Core.Kit;

/// <summary>Settings of one piece: detection + output gain.</summary>
public sealed record PieceSettings
{
    /// <summary>Detection and player settings (normalized when applied).</summary>
    public TriggerSettings Trigger { get; init; } = TriggerSettings.Default;

    /// <summary>Gain (dB) of this piece's return, applied on top of velocity and of Trigger.OutputGainDb.</summary>
    public float OutputGainDb { get; init; }

    /// <summary>Starting point for a piece (see <see cref="KitDefaults"/>).</summary>
    public static PieceSettings DefaultFor(DrumPiece p) => new() { Trigger = KitDefaults.TriggerFor(p) };
}

/// <summary>Settings of the whole kit: one <see cref="PieceSettings"/> per piece, in <see cref="DrumPiece"/> order.</summary>
public sealed class KitSettings : IEquatable<KitSettings>
{
    private readonly PieceSettings[] _pieces;

    public KitSettings(IReadOnlyList<PieceSettings> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count != DrumPieces.Count)
            throw new ArgumentException($"São necessárias {DrumPieces.Count} peças; recebidas {pieces.Count}.", nameof(pieces));
        _pieces = new PieceSettings[DrumPieces.Count];
        for (int i = 0; i < _pieces.Length; i++) _pieces[i] = pieces[i] ?? throw new ArgumentNullException(nameof(pieces));
    }

    /// <summary>Per-piece settings in <see cref="DrumPiece"/> order.</summary>
    public IReadOnlyList<PieceSettings> Pieces => _pieces;

    public PieceSettings this[DrumPiece p] => _pieces[(int)p];

    /// <summary>Copy with one piece replaced.</summary>
    public KitSettings With(DrumPiece p, PieceSettings s)
    {
        var copy = (PieceSettings[])_pieces.Clone();
        copy[(int)p] = s ?? throw new ArgumentNullException(nameof(s));
        return new KitSettings(copy);
    }

    /// <summary>Per-piece defaults (<see cref="PieceSettings.DefaultFor"/>).</summary>
    public static KitSettings Default { get; } = new(DrumPieces.All.Select(PieceSettings.DefaultFor).ToArray());

    public bool Equals(KitSettings? other) => other is not null && _pieces.SequenceEqual(other._pieces);
    public override bool Equals(object? obj) => Equals(obj as KitSettings);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var p in _pieces) h.Add(p);
        return h.ToHashCode();
    }
}

/// <summary>
/// Per-piece detection starting points (to be calibrated with real recordings; see docs/README-dev.md).
/// </summary>
public static class KitDefaults
{
    /// <summary>Detection defaults of a piece. Kick = v1 defaults.</summary>
    public static TriggerSettings TriggerFor(DrumPiece p) => p switch
    {
        DrumPiece.Kick => TriggerSettings.Default,
        // STEP 0 placeholder: tuned per piece in the implementation step.
        _ => TriggerSettings.Default,
    };
}
