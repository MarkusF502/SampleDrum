using System.Threading.Channels;

namespace SampleDrum.Core.Kit;

/// <summary>Life cycle of a <see cref="KitOperation"/>. Applied/Failed/Superseded are terminal.</summary>
public enum KitOperationStatus { Queued, Preparing, Applied, Failed, Superseded }
/// <summary>Kinds of controller operations. <see cref="Preset"/> and <see cref="PreparedKit"/> replace the whole kit.</summary>
public enum KitOperationKind { PieceSample, Preset, PieceEnabled, PieceGain, KitEnabled, PieceSettings, PreparedKit, LibraryFolder }

public sealed record KitOperationSnapshot(Guid Id, KitOperationKind Kind, DrumPiece? Piece,
    KitOperationStatus Status, IReadOnlyList<PieceLoadError> Errors, long? ResultVersion);

/// <summary>Thread-safe operation handle; Completion resolves at a terminal status, including failures.</summary>
public sealed class KitOperation
{
    private KitOperationSnapshot _snapshot;
    private readonly TaskCompletionSource<KitOperationSnapshot> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal KitOperation(KitOperationKind kind, DrumPiece? piece) =>
        _snapshot = new(Guid.NewGuid(), kind, piece, KitOperationStatus.Queued, Array.Empty<PieceLoadError>(), null);
    public Guid Id => Snapshot.Id;
    public KitOperationSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Task<KitOperationSnapshot> Completion => _completion.Task;
    internal void Update(KitOperationStatus status, IReadOnlyList<PieceLoadError>? errors = null, long? version = null)
    {
        var next = Snapshot with { Status = status, Errors = Array.AsReadOnly((errors ?? []).ToArray()), ResultVersion = version };
        Volatile.Write(ref _snapshot, next);
        if (status is KitOperationStatus.Applied or KitOperationStatus.Failed or KitOperationStatus.Superseded)
            _completion.TrySetResult(next);
    }
}

public sealed record KitPieceSnapshot(DrumPiece Piece, string? SampleId, string? SamplePath, float GainDb,
    bool Enabled, PieceSettings Settings);
public sealed record KitStateSnapshot(long Version, bool KitEnabled, string? ActivePresetId,
    IReadOnlyList<KitPieceSnapshot> Pieces);

/// <summary>
/// Single command path for PC and remote adapters. Owns engine changes for its lifetime; do not also mutate the
/// engine directly. Preparation and notifications run on a serialized worker, never on the audio thread.
/// Applied means submitted to the engine (also while stopped); observe KitEngine.AppliedKitGeneration for audio
/// acknowledgement. Each successful operation increments Version once. Overlapping newer requests supersede
/// pending work, including in-flight preparation, before it can touch the engine. Failed preparation changes nothing.
/// <para>
/// Last-writer-wins is directional: an operation only supersedes older pending ones it fully covers. A whole-kit
/// change (<see cref="ApplyPreset"/>, <see cref="ApplyKit"/>) supersedes older whole-kit changes and older sample
/// changes; a sample change supersedes older sample changes of the same piece only (it never cancels a pending
/// preset, whose other pieces would be lost); enable/gain/settings/kit-enable supersede the same kind for the same
/// target. Everything else is applied in submission order.
/// </para>
/// <para>Only the last <see cref="OperationHistory"/> finished operations stay queryable through <see cref="GetOperation"/>.</para>
/// </summary>
public sealed class KitController : IAsyncDisposable
{
    private readonly KitEngine _engine;
    private SampleLibrary _library;        // replaced only by a LibraryFolder operation (worker, under the gate)
    private readonly object _gate = new(); // controller callers/worker only; never used by Process
    private readonly Channel<Request> _queue = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Dictionary<Guid, KitOperation> _operations = [];
    private readonly Queue<Guid> _finished = new();   // terminal operations, oldest first (bounded history)
    private readonly List<Request> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<KitPreset, int, CancellationToken, Task<KitPrepareResult>> _prepare;
    private KitStateSnapshot _snapshot;
    private bool _disposed;

    public KitController(KitEngine engine, SampleLibrary library) : this(engine, library, null) { }

    internal KitController(KitEngine engine, SampleLibrary library,
        Func<KitPreset, int, CancellationToken, Task<KitPrepareResult>>? prepare)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        // Default: always the CURRENT library (it can change with SetLibraryFolder).
        _prepare = prepare ?? ((preset, rate, ct) => Library.PrepareAsync(preset, rate, ct));
        _snapshot = Capture(0);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised by the worker, outside controller locks, on state or operation transitions. Read Snapshot/GetOperation.</summary>
    public event EventHandler? StateChanged;
    public KitStateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public long Version => Snapshot.Version;
    public KitOperationSnapshot? GetOperation(Guid id)
    { lock (_gate) return _operations.TryGetValue(id, out var op) ? op.Snapshot : null; }

    /// <summary>Current sample library (changes only through <see cref="SetLibraryFolder"/>).</summary>
    public SampleLibrary Library { get { lock (_gate) return _library; } }

    /// <summary>Test hook: called on the worker right before an operation touches the engine.</summary>
    internal Action<KitOperationSnapshot>? BeforeApply { get; set; }

    /// <summary>Untrusted library id; null removes the sample. Outside-library ids fail with a stable code.</summary>
    public KitOperation SetPieceSample(DrumPiece piece, string? sampleId) =>
        Enqueue(KitOperationKind.PieceSample, piece, sampleId, remoteId: true);

    /// <summary>Trusted PC picker path (absolute paths allowed); remote adapters must use SetPieceSample.</summary>
    public KitOperation SetPieceSamplePath(DrumPiece piece, string? samplePath) =>
        Enqueue(KitOperationKind.PieceSample, piece, samplePath);

    public KitOperation ApplyPreset(KitPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Pieces is null)
            return Enqueue(KitOperationKind.Preset, null, null, failure: Invalid(null, "Preset sem a lista de peças."));
        return Enqueue(KitOperationKind.Preset, null, preset with { Pieces = new Dictionary<DrumPiece, PiecePreset>(preset.Pieces) });
    }

    /// <summary>
    /// Changes the samples folder as a regular operation (Version keeps increasing, same controller and subscribers).
    /// Later sample/preset operations resolve against the new folder.
    /// </summary>
    public KitOperation SetLibraryFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Enqueue(KitOperationKind.LibraryFolder, null, null, failure: Invalid(null, "Pasta de samples vazia."));
        return Enqueue(KitOperationKind.LibraryFolder, null, folder);
    }
    public KitOperation SetPieceEnabled(DrumPiece piece, bool enabled) => Enqueue(KitOperationKind.PieceEnabled, piece, enabled);
    public KitOperation SetPieceGain(DrumPiece piece, float gainDb) =>
        float.IsFinite(gainDb)
            ? Enqueue(KitOperationKind.PieceGain, piece, gainDb)
            : Enqueue(KitOperationKind.PieceGain, piece, null, failure: Invalid(piece, $"Ganho inválido ({gainDb})."));
    public KitOperation SetKitEnabled(bool enabled) => Enqueue(KitOperationKind.KitEnabled, null, enabled);

    /// <summary>Detection + gain of one piece (normalized when applied).</summary>
    public KitOperation SetPieceSettings(DrumPiece piece, PieceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? problem = Validate(settings);
        return problem is null
            ? Enqueue(KitOperationKind.PieceSettings, piece, settings with { Trigger = settings.Trigger.Normalized() })
            : Enqueue(KitOperationKind.PieceSettings, piece, null, failure: Invalid(piece, problem));
    }

    /// <summary>
    /// Applies an already prepared kit (e.g. the synthetic simulation kit) as a whole-kit change; optional per-piece
    /// settings are applied with it. Sample rate mismatch fails the operation (the engine stays untouched).
    /// </summary>
    public KitOperation ApplyKit(PreparedKit kit, KitSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(kit);
        string? problem = settings is null ? null
            : DrumPieces.All.Select(p => Validate(settings[p]) is { } m ? $"{p.Label()}: {m}" : null).FirstOrDefault(m => m is not null);
        return problem is null
            ? Enqueue(KitOperationKind.PreparedKit, null, (kit, settings))
            : Enqueue(KitOperationKind.PreparedKit, null, null, failure: Invalid(null, problem));
    }

    /// <summary>Null when the settings can be applied; otherwise a pt-BR reason.</summary>
    private static string? Validate(PieceSettings s) =>
        s.Trigger is null ? "ajustes sem os valores de detecção (Trigger nulo)."
        : !float.IsFinite(s.OutputGainDb) ? $"ganho inválido ({s.OutputGainDb})."
        : null;

    private static PieceLoadError Invalid(DrumPiece? piece, string message) =>
        new(piece ?? DrumPiece.Kick, null, message) { Code = PieceLoadErrorCode.InvalidArgument };

    /// <summary>Number of finished operations kept for <see cref="GetOperation"/>.</summary>
    public const int OperationHistory = 256;

    private KitOperation Enqueue(KitOperationKind kind, DrumPiece? piece, object? value, bool remoteId = false,
        PieceLoadError? failure = null)
    {
        if (piece.HasValue && !piece.Value.IsValid()) throw new ArgumentOutOfRangeException(nameof(piece));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var op = new KitOperation(kind, piece);
            var request = new Request(op, value, remoteId) { Failure = failure };
            // An invalid request fails on the worker (in order) and never supersedes valid pending work.
            if (failure is null)
                foreach (var older in _pending)
                    if (Supersedes(op.Snapshot, older.Op.Snapshot)) older.Superseded = true;
            _pending.Add(request);
            _operations.Add(op.Id, op);
            _queue.Writer.TryWrite(request);
            return op;
        }
    }

    /// <summary>True when <paramref name="newer"/> fully covers <paramref name="older"/> (see class remarks).</summary>
    internal static bool Supersedes(KitOperationSnapshot newer, KitOperationSnapshot older)
    {
        static bool WholeKit(KitOperationKind k) => k is KitOperationKind.Preset or KitOperationKind.PreparedKit;
        if (WholeKit(newer.Kind)) return WholeKit(older.Kind) || older.Kind == KitOperationKind.PieceSample;
        return newer.Kind switch
        {
            KitOperationKind.PieceSample => older.Kind == KitOperationKind.PieceSample && older.Piece == newer.Piece,
            KitOperationKind.PieceEnabled => older.Kind == KitOperationKind.PieceEnabled && older.Piece == newer.Piece,
            KitOperationKind.PieceGain => older.Kind == KitOperationKind.PieceGain && older.Piece == newer.Piece,
            // Settings carry the gain too, so they also cover an older gain change of the same piece.
            KitOperationKind.PieceSettings => older.Kind is KitOperationKind.PieceSettings or KitOperationKind.PieceGain
                                              && older.Piece == newer.Piece,
            KitOperationKind.KitEnabled => older.Kind == KitOperationKind.KitEnabled,
            KitOperationKind.LibraryFolder => older.Kind == KitOperationKind.LibraryFolder,
            _ => false,
        };
    }

    private async Task RunAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await ProcessAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Last line of defence: one broken operation must never stop the queue.
                lock (_gate)
                {
                    if (request.Op.Snapshot.Status is KitOperationStatus.Queued or KitOperationStatus.Preparing)
                        request.Op.Update(KitOperationStatus.Failed, [Internal(request.Op.Snapshot, ex)]);
                    _pending.Remove(request);
                    Retire(request.Op);
                }
                Notify();
            }
        }
    }

    private static PieceLoadError Internal(KitOperationSnapshot s, Exception ex) =>
        new(s.Piece ?? DrumPiece.Kick, null, $"Falha inesperada ao aplicar a operação: {ex.Message}") { Code = PieceLoadErrorCode.Internal };

    private async Task ProcessAsync(Request request)
    {
        var op = request.Op;
        lock (_gate)
        {
            if (request.Superseded || _disposed) op.Update(KitOperationStatus.Superseded);
            else op.Update(KitOperationStatus.Preparing);
        }
        Notify();
        if (op.Snapshot.Status == KitOperationStatus.Superseded)
        { lock (_gate) { _pending.Remove(request); Retire(op); } return; }
        KitPrepareResult? result = null;
        SampleLibrary? newLibrary = null;
        IReadOnlyList<PieceLoadError>? errors = request.Failure is { } f ? [f] : null;
        if (errors is null)
        {
            try
            {
                var s = op.Snapshot;
                var library = Library;
                if (s.Kind == KitOperationKind.Preset)
                    result = await _prepare((KitPreset)request.Value!, _engine.SampleRate, _stop.Token).ConfigureAwait(false);
                else if (s.Kind == KitOperationKind.LibraryFolder)
                    newLibrary = new SampleLibrary((string)request.Value!);
                else if (s.Kind == KitOperationKind.PreparedKit)
                {
                    var (kit, _) = ((PreparedKit, KitSettings?))request.Value!;
                    if (kit.SampleRate != _engine.SampleRate)
                        errors = [new PieceLoadError(DrumPiece.Kick, null,
                            $"Kit preparado a {kit.SampleRate} Hz; o motor roda a {_engine.SampleRate} Hz.") { Code = PieceLoadErrorCode.UnsupportedFormat }];
                    else result = new KitPrepareResult(kit, [], []);
                }
                else if (s.Kind == KitOperationKind.PieceSample)
                {
                    string? path = (string?)request.Value;
                    if (request.RemoteId && path is not null)
                    {
                        if (!library.TryResolveInLibrary(path, out var resolved))
                            errors = [new PieceLoadError(s.Piece!.Value, path, "Sample fora da biblioteca.")
                                { Code = PieceLoadErrorCode.OutsideLibrary }];
                        else path = resolved;
                    }
                    if (errors is null)
                        result = await library.PreparePieceAsync(_engine.ActiveKit ?? PreparedKit.Empty(_engine.SampleRate),
                            s.Piece!.Value, path, _stop.Token).ConfigureAwait(false);
                }
                if (result is { Success: false }) errors = result.Errors;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { request.Superseded = true; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       && op.Snapshot.Kind == KitOperationKind.LibraryFolder)
            {
                errors = [new PieceLoadError(DrumPiece.Kick, (string?)request.Value, $"Pasta de samples inválida: {ex.Message}")
                    { Code = PieceLoadErrorCode.InvalidArgument }];
            }
            catch (Exception ex)
            {
                errors = [new PieceLoadError(op.Snapshot.Piece ?? DrumPiece.Kick, null, $"Não foi possível preparar a operação: {ex.Message}")
                    { Code = PieceLoadErrorCode.IoError }];
            }
        }
        lock (_gate)
        {
            if (request.Superseded || _disposed) op.Update(KitOperationStatus.Superseded);
            else if (errors is not null) op.Update(KitOperationStatus.Failed, errors);
            else
            {
                try
                {
                    BeforeApply?.Invoke(op.Snapshot);
                    if (result?.Kit is { } kit)
                    {
                        // A custom piece selection no longer represents an exact preset.
                        if (op.Snapshot.Kind == KitOperationKind.PieceSample)
                            kit = new PreparedKit(kit.Name, kit.SampleRate, kit.Pieces, null);
                        _engine.ApplyKit(kit);
                    }
                    var s = op.Snapshot;
                    if (newLibrary is not null) _library = newLibrary;
                    if (s.Kind == KitOperationKind.PreparedKit && (((PreparedKit, KitSettings?))request.Value!).Item2 is { } ks)
                        foreach (var piece in DrumPieces.All) _engine.UpdatePieceSettings(piece, ks[piece]);
                    if (s.Kind == KitOperationKind.PieceSettings)
                        _engine.UpdatePieceSettings(s.Piece!.Value, (PieceSettings)request.Value!);
                    if (s.Kind == KitOperationKind.PieceEnabled) _engine.SetPieceEnabled(s.Piece!.Value, (bool)request.Value!);
                    if (s.Kind == KitOperationKind.PieceGain)
                        _engine.UpdatePieceSettings(s.Piece!.Value, _engine.Settings[s.Piece.Value] with { OutputGainDb = (float)request.Value! });
                    if (s.Kind == KitOperationKind.KitEnabled) _engine.KitEnabled = (bool)request.Value!;
                    var snapshot = Capture(Version + 1);
                    Volatile.Write(ref _snapshot, snapshot);
                    op.Update(KitOperationStatus.Applied, version: snapshot.Version);
                }
                catch (Exception ex)
                {
                    // The engine may have been partly changed: publish what it really holds (Version moves so that
                    // observers re-read), fail only this operation and keep the worker running.
                    Volatile.Write(ref _snapshot, Capture(Version + 1));
                    op.Update(KitOperationStatus.Failed, [Internal(op.Snapshot, ex)]);
                }
            }
            _pending.Remove(request);
            Retire(op);
        }
        Notify();
    }

    /// <summary>Keeps only the last <see cref="OperationHistory"/> finished operations (caller holds the gate).</summary>
    private void Retire(KitOperation op)
    {
        _finished.Enqueue(op.Id);
        while (_finished.Count > OperationHistory) _operations.Remove(_finished.Dequeue());
    }

    private KitStateSnapshot Capture(long version)
    {
        var settings = _engine.Settings;
        var kit = _engine.ActiveKit;
        return new(version, _engine.KitEnabled, kit?.PresetId, Array.AsReadOnly(DrumPieces.All.Select(p =>
        {
            var path = kit?[p].SourcePath;
            string? id = path is null ? null : Path.GetRelativePath(_library.Folder, path).Replace('\\', '/');
            if (id is not null && !_library.TryResolveInLibrary(id, out _)) id = null;
            return new KitPieceSnapshot(p, id, path, settings[p].OutputGainDb, _engine.IsPieceEnabled(p), settings[p]);
        }).ToArray()));
    }

    private void Notify()
    {
        // A broken UI/subscriber must not stop the command queue or another subscriber.
        foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? [])
            try { handler(this, EventArgs.Empty); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed) { _disposed = true; _queue.Writer.TryComplete(); _stop.Cancel(); }
        }
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class Request(KitOperation op, object? value, bool remoteId)
    {
        public KitOperation Op { get; } = op;
        public object? Value { get; } = value;
        public bool RemoteId { get; } = remoteId;
        public PieceLoadError? Failure { get; init; }   // rejected at enqueue (invalid input): fails in order
        public bool Superseded;
    }
}
