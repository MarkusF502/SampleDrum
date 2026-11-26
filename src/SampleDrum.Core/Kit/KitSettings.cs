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
/// Per-piece detection starting points, derived from the rules in docs/research/lsp-trigger.md (envelope release
/// longer than one period of the fundamental, threshold at least ~10 dB above the bleed, high-pass below the
/// piece's fundamental). They must be calibrated with real recordings (see docs/README-dev.md).
/// </summary>
public static class KitDefaults
{
    /// <summary>
    /// Snare: fundamental ~180-250 Hz (period 4-5.5 ms) with a sharp attack; the snare mic picks up a lot of kick
    /// and hi-hat, so the high-pass is 100 Hz (removes most kick bleed) and the threshold is higher than the kick's.
    /// Rolls, drags and flams are fast, so a shorter lockout (25 ms) and a 10 ms envelope release (2 periods);
    /// ghost notes need a wider velocity range (30 dB).
    /// </summary>
    public static TriggerSettings Snare { get; } = TriggerSettings.Default.WithThreshold(-20f) with
    {
        HighPassHz = 100f,
        EnvelopeReleaseMs = 10f,
        RearmTimeMs = 10f,
        MinIntervalMs = 25f,
        VelocityRangeDb = 30f,
    };

    /// <summary>
    /// Rack tom: fundamental ~100-150 Hz (period 7-10 ms). Toms are played rarely and their mics hear the whole kit,
    /// so the threshold is high (-20 dBFS); 60 Hz high-pass against kick rumble, 15 ms envelope release,
    /// 30 ms lockout for 16th-note fills.
    /// </summary>
    public static TriggerSettings Tom { get; } = TriggerSettings.Default.WithThreshold(-20f) with
    {
        HighPassHz = 60f,
        EnvelopeReleaseMs = 15f,
        RearmTimeMs = 15f,
        MinIntervalMs = 30f,
    };

    /// <summary>
    /// Floor tom (surdo): fundamental ~70-100 Hz (period 10-14 ms), close to the kick: 50 Hz high-pass, 20 ms
    /// envelope release (as the kick), threshold -22 dBFS (bleed between kick and floor tom is strong), 30 ms lockout.
    /// </summary>
    public static TriggerSettings FloorTom { get; } = TriggerSettings.Default.WithThreshold(-22f) with
    {
        HighPassHz = 50f,
        EnvelopeReleaseMs = 20f,
        RearmTimeMs = 15f,
        MinIntervalMs = 30f,
    };

    /// <summary>Detection defaults of a piece. Kick = v1 defaults (<see cref="TriggerSettings.Default"/>).</summary>
    public static TriggerSettings TriggerFor(DrumPiece p) => p switch
    {
        DrumPiece.Kick => TriggerSettings.Default,
        DrumPiece.Snare => Snare,
        DrumPiece.Tom => Tom,
        DrumPiece.FloorTom => FloorTom,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };
}
