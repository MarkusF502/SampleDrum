namespace SampleDrum.Core.Kit;

/// <summary>A WAV file found in the samples folder.</summary>
public sealed record SampleInfo(string Name, string Path, double DurationSeconds, int SampleRate, int Channels);

/// <summary>Why one piece of a kit could not be prepared.</summary>
public sealed record PieceLoadError(DrumPiece Piece, string? Path, string Message);

/// <summary>
/// Result of <see cref="SampleLibrary.PrepareAsync"/>: either a complete <see cref="Kit"/>, or per-piece
/// <see cref="Errors"/> and no kit (a failed prepare must leave the active kit untouched).
/// </summary>
public sealed record KitPrepareResult(PreparedKit? Kit, IReadOnlyList<PieceLoadError> Errors, IReadOnlyList<string> Notes)
{
    public bool Success => Kit is not null && Errors.Count == 0;
}

/// <summary>
/// Sample folder access and kit preparation. Everything here runs OFF the audio thread (file I/O, decoding,
/// resampling, sanitizing) and never touches a running engine: the caller applies the result with
/// <see cref="KitEngine.ApplyKit"/>.
/// </summary>
public sealed class SampleLibrary
{
    public SampleLibrary(string folder)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    /// <summary>Samples folder (relative preset paths are resolved against it).</summary>
    public string Folder { get; }

    /// <summary>Lists *.wav files under <see cref="Folder"/> (recursive), sorted by name; unreadable files are skipped.</summary>
    public IReadOnlyList<SampleInfo> List() => throw new NotImplementedException();

    /// <summary>Absolute path for a preset path (absolute paths are returned unchanged).</summary>
    public string ResolvePath(string path) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder, path));

    /// <summary>Loads, downmixes, resamples and sanitizes every sample of <paramref name="preset"/>.</summary>
    public Task<KitPrepareResult> PrepareAsync(KitPreset preset, int sampleRate, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <summary>
    /// Single-piece change: prepares one sample and returns <paramref name="current"/> with that piece replaced
    /// (or errors, leaving nothing changed). <paramref name="samplePath"/> null = remove the piece's sample.
    /// </summary>
    public Task<KitPrepareResult> PreparePieceAsync(PreparedKit current, DrumPiece piece, string? samplePath,
        CancellationToken ct = default) => throw new NotImplementedException();
}
