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
    public static KitPreset FromJson(string json)
    {
        KitPreset? preset;
        try { preset = JsonSerializer.Deserialize<KitPreset>(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException($"Preset inválido: {ex.Message}", ex); }
        if (preset is null) throw new InvalidDataException("Preset vazio.");
        if (preset.PresetVersion > CurrentVersion)
            throw new InvalidDataException($"Preset de versão {preset.PresetVersion}; este programa lê até a versão {CurrentVersion}.");
        foreach (var key in preset.Pieces.Keys)
            if (!key.IsValid()) throw new InvalidDataException($"Peça inválida no preset: {key}.");
        return preset with { Pieces = new Dictionary<DrumPiece, PiecePreset>(preset.Pieces) };
    }

    /// <summary>Loads a preset file; an empty name falls back to the file name.</summary>
    public static KitPreset Load(string path)
    {
        var preset = FromJson(File.ReadAllText(path));
        return string.IsNullOrWhiteSpace(preset.Name) ? preset with { Name = Path.GetFileNameWithoutExtension(path) } : preset;
    }

    /// <summary>Writes the preset atomically (temp file + move).</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads every *.json preset in <paramref name="folder"/>; unreadable files are reported, not thrown.</summary>
    public static IReadOnlyList<KitPreset> LoadFolder(string folder, out IReadOnlyList<string> errors)
    {
        var list = new List<KitPreset>();
        var errs = new List<string>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { list.Add(Load(file)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    errs.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        errors = errs;
        return list;
    }

    /// <summary>File name for a preset name (invalid characters replaced).</summary>
    public static string FileNameFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((string.IsNullOrWhiteSpace(name) ? "preset" : name.Trim())
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean + ".json";
    }
}
