namespace SampleDrum.Core.Kit;

/// <summary>Persisted settings of one piece.</summary>
public sealed class PieceConfig
{
    public DrumPiece Piece { get; set; }

    /// <summary>Per-piece enable (persisted). The kit master enable is never persisted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Selected sample (absolute, or relative to <see cref="AppConfig.SamplesFolder"/>); null = none.</summary>
    public string? SamplePath { get; set; }

    /// <summary>Return gain (dB); null = 0 dB.</summary>
    public float? OutputGainDb { get; set; }

    /// <summary>Detection values that differ from <see cref="KitDefaults.TriggerFor"/> (sparse).</summary>
    public TriggerOverrides? Trigger { get; set; }

    /// <summary>"Avançado" expanded in this piece's panel.</summary>
    public bool AdvancedExpanded { get; set; }
}

/// <summary>
/// App configuration v2 (System.Text.Json), file %AppData%\SampleDrum\settings.json. Loading a v1 file migrates it
/// into the Kick piece and the church profile's kick route and keeps a backup settings.v1.json.
/// Automatic triggering (kit enable) is intentionally not persisted: the app always starts disabled.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 2;
    public const string FileName = "settings.json";
    public const string V1BackupFileName = "settings.v1.json";

    public int SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>ASIO driver name (kept while the simulation is selected).</summary>
    public string? DriverName { get; set; }
    public bool UseSimulation { get; set; } = true;

    /// <summary>Name of the active routing profile (one of <see cref="Profiles"/>).</summary>
    public string ActiveProfileName { get; set; } = RoutingProfiles.ChurchName;

    /// <summary>Editable routing profiles (starts with the built-in Igreja and Simulação).</summary>
    public List<RoutingProfile> Profiles { get; set; } = RoutingProfiles.BuiltIn.ToList();

    /// <summary>Exactly one entry per piece after <see cref="Normalize"/>.</summary>
    public List<PieceConfig> Pieces { get; set; } = DrumPieces.All.Select(p => new PieceConfig { Piece = p }).ToList();

    public string? PresetsFolder { get; set; }
    public string? SamplesFolder { get; set; }

    /// <summary>Default folder: %AppData%\SampleDrum.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SampleDrum");

    public static string DefaultFilePath { get; } = Path.Combine(DefaultDirectory, FileName);

    public PieceConfig this[DrumPiece p] => Pieces.First(x => x.Piece == p);

    /// <summary>The active profile (falls back to the church profile).</summary>
    public RoutingProfile ActiveProfile =>
        Profiles.FirstOrDefault(x => x.Name == ActiveProfileName) ?? RoutingProfiles.Church;

    /// <summary>Kit settings = per-piece defaults + stored overrides and gains.</summary>
    public KitSettings ToKitSettings() => throw new NotImplementedException();

    /// <summary>Stores a piece's settings sparsely (values equal to the defaults are not written).</summary>
    public void SetPieceSettings(DrumPiece p, PieceSettings s) => throw new NotImplementedException();

    /// <summary>Ensures 4 pieces, the built-in profiles and a valid active profile.</summary>
    public void Normalize() => throw new NotImplementedException();

    public static AppConfig Load(out string? message) => Load(DefaultFilePath, out message);

    /// <summary>
    /// Loads <paramref name="path"/>. Missing → defaults. Corrupt → defaults + pt-BR message (file left as is).
    /// v1 → migrated, original copied to settings.v1.json next to it, pt-BR message.
    /// </summary>
    public static AppConfig Load(string path, out string? message) => throw new NotImplementedException();

    /// <summary>Parses JSON without file I/O (v1 JSON is migrated). Throws on invalid JSON.</summary>
    public static AppConfig Parse(string json, out string? message) => throw new NotImplementedException();

    /// <summary>Migrates a v1 settings.json (src/SampleDrum.App/AppSettings.cs) into a v2 config.</summary>
    public static AppConfig MigrateFromV1(string v1Json, out string note) => throw new NotImplementedException();

    public string ToJson() => throw new NotImplementedException();

    public void Save() => Save(DefaultFilePath);

    /// <summary>Writes atomically (temp file + move).</summary>
    public void Save(string path) => throw new NotImplementedException();
}
