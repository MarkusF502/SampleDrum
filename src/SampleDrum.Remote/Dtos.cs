using System.Text.Json;
using System.Text.Json.Serialization;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

// Wire contract of the phone panel (docs/painel-api.md). JSON camelCase; null members are omitted only where the
// contract says "optional" (marked with JsonIgnore WhenWritingNull); everything else is always present.

/// <summary>POST /api/pair body.</summary>
public sealed record PairRequest(string? Code, string? DeviceName);

/// <summary>POST /api/pieces/{piece}/sample body.</summary>
public sealed record PieceSampleRequest(string? SampleId, string? ClientOpId, long? BaseVersion);

/// <summary>POST /api/kit/preset body.</summary>
public sealed record PresetRequest(string? PresetId, string? ClientOpId, long? BaseVersion);

/// <summary>POST /api/pieces/{piece}/test body.</summary>
public sealed record TestRequest(string? ClientOpId);

/// <summary>202 body of every accepted command.</summary>
public sealed record OperationAccepted(string OpId);

/// <summary>GET /api/samples item.</summary>
public sealed record SampleDto(string Id, string Name, int DurationMs, int SampleRate, int Channels);

/// <summary>One piece of a preset in GET /api/presets (sampleId null = no sample or file outside the library).</summary>
public sealed record PresetPieceDto(
    string? SampleId,
    string? SampleName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GainDb);

/// <summary>GET /api/presets item. Pieces always has the four keys kick, snare, tom, floorTom.</summary>
public sealed record PresetDto(string Id, string Name, IReadOnlyDictionary<string, PresetPieceDto> Pieces, bool Available);

/// <summary>Sample currently loaded in a piece.</summary>
public sealed record PieceSampleDto(string? Id, string Name, int? DurationMs);

/// <summary>One piece in the state snapshot.</summary>
public sealed record PieceStateDto(
    string Piece,
    string Label,
    bool Enabled,
    string Route,
    PieceSampleDto? Sample,
    float OutputGainDb,
    long SampleVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PendingOpId);

public sealed record KitInfoDto(string Name, string? PresetId);

public sealed record DeviceInfoDto(string Name);

/// <summary>GET /api/state and SSE `state` payload.</summary>
public sealed record StateDto(
    long Version,
    long KitVersion,
    string Profile,
    bool Simulation,
    bool AudioRunning,
    bool KitEnabled,
    KitInfoDto Kit,
    IReadOnlyList<PieceStateDto> Pieces,
    bool AllowPhoneTest,
    DeviceInfoDto Device);

/// <summary>Per-piece error of a failed operation.</summary>
public sealed record OperationErrorDto(string? Piece, string Code, string Message);

/// <summary>GET /api/operations/{opId} and SSE `op` payload.</summary>
public sealed record OperationDto(
    string OpId,
    string? ClientOpId,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Piece,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SampleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PresetId,
    string Status,
    bool EngineConfirmed,
    bool AudioRunning,
    IReadOnlyList<OperationErrorDto> Errors,
    long? ResultVersion);

/// <summary>SSE `hit` payload.</summary>
public sealed record HitDto(string Piece, float Velocity, bool Test);

/// <summary>application/problem+json body. <see cref="State"/> is set only for 409 stale_state.</summary>
public sealed record ProblemDto(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateDto? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds = null);

/// <summary>Wire names and JSON options shared by the server and the tests.</summary>
public static class Wire
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>kick, snare, tom, floorTom.</summary>
    public static string PieceId(DrumPiece p) => p switch
    {
        DrumPiece.Kick => "kick",
        DrumPiece.Snare => "snare",
        DrumPiece.Tom => "tom",
        DrumPiece.FloorTom => "floorTom",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Exact (case-sensitive) piece id; false for anything else.</summary>
    public static bool TryParsePiece(string? id, out DrumPiece piece)
    {
        foreach (var p in DrumPieces.All)
            if (string.Equals(PieceId(p), id, StringComparison.Ordinal)) { piece = p; return true; }
        piece = default;
        return false;
    }

    public static string Status(KitOperationStatus s) => s switch
    {
        KitOperationStatus.Queued => "queued",
        KitOperationStatus.Preparing => "preparing",
        KitOperationStatus.Applied => "applied",
        KitOperationStatus.Failed => "failed",
        KitOperationStatus.Superseded => "superseded",
        _ => "failed",
    };

    /// <summary>
    /// pt-BR message for the phone, built from the stable code and the sample FILE NAME only. Never Core's message:
    /// it carries the PC's absolute path and repeats the piece label (the piece is its own field).
    /// </summary>
    public static string PhoneMessage(PieceLoadError e)
    {
        string? file = SafeFileName(e.Path);
        string named = file is null ? "" : $" ({file})";
        return e.Code switch
        {
            PieceLoadErrorCode.FileNotFound => $"arquivo não encontrado na pasta de samples do PC{named}.",
            PieceLoadErrorCode.UnsupportedFormat => $"arquivo de áudio inválido ou não suportado{named}.",
            PieceLoadErrorCode.TooLong => $"sample longo demais{named}; máximo {SampleLibrary.MaxSampleSeconds:0} s.",
            PieceLoadErrorCode.OutsideLibrary => "sample fora da biblioteca do PC.",
            PieceLoadErrorCode.IoError => $"não foi possível ler o sample{named}.",
            PieceLoadErrorCode.InvalidArgument => "pedido inválido.",
            _ => "falha inesperada no PC; o kit atual foi mantido.",
        };
    }

    /// <summary>File name of a path (no folders, no drive), or null.</summary>
    internal static string? SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string name = path.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        int colon = name.LastIndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        return name.Length == 0 ? null : name;
    }

    /// <summary>Stable snake_case code of a piece load error.</summary>
    public static string ErrorCode(PieceLoadErrorCode c) => c switch
    {
        PieceLoadErrorCode.FileNotFound => "file_not_found",
        PieceLoadErrorCode.UnsupportedFormat => "unsupported_format",
        PieceLoadErrorCode.TooLong => "too_long",
        PieceLoadErrorCode.OutsideLibrary => "outside_library",
        PieceLoadErrorCode.IoError => "io_error",
        PieceLoadErrorCode.InvalidArgument => "invalid_argument",
        _ => "internal",
    };
}
