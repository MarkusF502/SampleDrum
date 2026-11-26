using System.Text.Json;
using System.Text.Json.Serialization;

namespace SampleDrum.Core.Kit;

/// <summary>Preset of one piece: sample, gain and partial detection overrides (all optional).</summary>
/// <param name="SamplePath">WAV path (absolute, or relative to the samples folder); null = no sample.</param>
/// <param name="GainDb">Output gain (dB); null = keep the current gain.</param>
/// <param name="Detection">Partial detection settings; null = keep the current settings.</param>
public sealed record PiecePreset(string? SamplePath = null, float? GainDb = null, TriggerOverrides? Detection = null);

/// <summary>Kit preset stored as a JSON file (one file per preset).</summary>
public sealed record KitPreset
{
    public const int CurrentVersion = 1;

    public int PresetVersion { get; init; } = CurrentVersion;
    public string Name { get; init; } = "";

    /// <summary>Pieces present in the preset; a missing piece means "no sample" for that piece.</summary>
    public IReadOnlyDictionary<DrumPiece, PiecePreset> Pieces { get; init; } = new Dictionary<DrumPiece, PiecePreset>();

    public PiecePreset? this[DrumPiece p] => Pieces.TryGetValue(p, out var v) ? v : null;

    /// <summary>JSON options shared by presets and <see cref="AppConfig"/> (indented, enums as names, nulls omitted).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a preset; throws <see cref="InvalidDataException"/> with a pt-BR message when invalid.</summary>
    public static KitPreset FromJson(string json) => throw new NotImplementedException();

    public static KitPreset Load(string path) => throw new NotImplementedException();

    /// <summary>Writes the preset atomically (temp file + move).</summary>
    public void Save(string path) => throw new NotImplementedException();

    /// <summary>Loads every *.json preset in <paramref name="folder"/>; unreadable files are reported, not thrown.</summary>
    public static IReadOnlyList<KitPreset> LoadFolder(string folder, out IReadOnlyList<string> errors) =>
        throw new NotImplementedException();
}
