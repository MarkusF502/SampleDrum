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
    /// <summary>Longest accepted sample (one-shot drum samples are short; protects memory from a wrong file).</summary>
    public const double MaxSampleSeconds = 20.0;

    public SampleLibrary(string folder)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    /// <summary>Samples folder (relative preset paths are resolved against it).</summary>
    public string Folder { get; }

    /// <summary>Lists *.wav files under <see cref="Folder"/> (recursive), sorted by name; unreadable files are skipped.</summary>
    public IReadOnlyList<SampleInfo> List()
    {
        var result = new List<SampleInfo>();
        if (!Directory.Exists(Folder)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(Folder, "*.wav", options))
        {
            try
            {
                var info = WavFile.ReadInfo(file);
                result.Add(new SampleInfo(System.IO.Path.GetFileNameWithoutExtension(file), System.IO.Path.GetFullPath(file),
                    info.DurationSeconds, info.SampleRate, info.Channels));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // Not a usable WAV: not listed.
            }
        }
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    /// <summary>Absolute path for a preset path (absolute paths are returned unchanged).</summary>
    public string ResolvePath(string path) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder, path));

    /// <summary>
    /// Loads, downmixes, resamples and sanitizes every sample of <paramref name="preset"/> on a worker thread.
    /// Any piece error → no kit (the caller keeps the active kit). Gains and detection overrides of the preset are
    /// carried in the kit and applied together with the samples.
    /// </summary>
    public Task<KitPrepareResult> PrepareAsync(KitPreset preset, int sampleRate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return Task.Run(() =>
        {
            var pieces = new List<PreparedPiece>();
            var errors = new List<PieceLoadError>();
            var notes = new List<string>();
            foreach (var p in DrumPieces.All)
            {
                ct.ThrowIfCancellationRequested();
                var pp = preset[p];
                var prepared = PreparedPiece.Empty(p);
                if (pp?.SamplePath is { Length: > 0 } path
                    && !TryLoad(p, path, sampleRate, errors, notes, out prepared))
                    continue;
                pieces.Add(prepared with { GainDb = pp?.GainDb, Detection = pp?.Detection });
            }
            ct.ThrowIfCancellationRequested();
            return errors.Count > 0
                ? new KitPrepareResult(null, errors, notes)
                : new KitPrepareResult(new PreparedKit(preset.Name, sampleRate, pieces), errors, notes);
        }, ct);
    }

    /// <summary>
    /// Single-piece change: prepares one sample and returns <paramref name="current"/> (samples only, so gains and
    /// settings are not reverted) with that piece replaced, or errors and no kit. <paramref name="samplePath"/>
    /// null = remove the piece's sample.
    /// </summary>
    public Task<KitPrepareResult> PreparePieceAsync(PreparedKit current, DrumPiece piece, string? samplePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var errors = new List<PieceLoadError>();
            var notes = new List<string>();
            var prepared = PreparedPiece.Empty(piece);
            if (samplePath is { Length: > 0 } && !TryLoad(piece, samplePath, current.SampleRate, errors, notes, out prepared))
                return new KitPrepareResult(null, errors, notes);
            return new KitPrepareResult(current.SamplesOnly().WithPiece(prepared), errors, notes);
        }, ct);
    }

    private bool TryLoad(DrumPiece p, string path, int sampleRate, List<PieceLoadError> errors, List<string> notes,
        out PreparedPiece prepared)
    {
        prepared = PreparedPiece.Empty(p);
        string full = ResolvePath(path);
        try
        {
            if (!File.Exists(full)) { errors.Add(new PieceLoadError(p, full, $"{p.Label()}: arquivo não encontrado: {full}")); return false; }
            var info = WavFile.ReadInfo(full);
            if (info.DurationSeconds > MaxSampleSeconds)
            {
                errors.Add(new PieceLoadError(p, full,
                    $"{p.Label()}: sample longo demais ({info.DurationSeconds:0.0} s; máximo {MaxSampleSeconds:0} s)."));
                return false;
            }
            var mono = WavFile.ReadMonoAtRate(full, sampleRate, out var note);
            if (mono.Length == 0) { errors.Add(new PieceLoadError(p, full, $"{p.Label()}: o arquivo não tem áudio.")); return false; }
            if (note != null) notes.Add($"{p.Label()}: {note}");
            prepared = new PreparedPiece(p, mono, full, System.IO.Path.GetFileNameWithoutExtension(full));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            errors.Add(new PieceLoadError(p, full, $"{p.Label()}: não foi possível ler {System.IO.Path.GetFileName(full)} ({ex.Message})."));
            return false;
        }
    }
}
