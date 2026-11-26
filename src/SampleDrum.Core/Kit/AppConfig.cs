using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const string InvalidBackupFileName = "settings.invalido.json";

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

    /// <summary>Presets folder; null = %AppData%\SampleDrum\Presets.</summary>
    public string? PresetsFolder { get; set; }

    /// <summary>Samples folder; null = %AppData%\SampleDrum\Samples.</summary>
    public string? SamplesFolder { get; set; }

    /// <summary>Default folder: %AppData%\SampleDrum.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SampleDrum");

    public static string DefaultFilePath { get; } = Path.Combine(DefaultDirectory, FileName);

    [JsonIgnore] public string EffectivePresetsFolder => PresetsFolder ?? Path.Combine(DefaultDirectory, "Presets");
    [JsonIgnore] public string EffectiveSamplesFolder => SamplesFolder ?? Path.Combine(DefaultDirectory, "Samples");

    public PieceConfig this[DrumPiece p] => Pieces.First(x => x.Piece == p);

    /// <summary>The active profile (falls back to the church profile).</summary>
    [JsonIgnore]
    public RoutingProfile ActiveProfile =>
        Profiles.FirstOrDefault(x => x.Name == ActiveProfileName) ?? RoutingProfiles.Church;

    /// <summary>Settings of one piece = piece defaults + stored overrides and gain.</summary>
    public PieceSettings GetPieceSettings(DrumPiece p)
    {
        var c = this[p];
        var defaults = KitDefaults.TriggerFor(p);
        return new PieceSettings
        {
            Trigger = c.Trigger?.ApplyTo(defaults) ?? defaults.Normalized(),
            OutputGainDb = c.OutputGainDb ?? 0f,
        };
    }

    /// <summary>Kit settings = per-piece defaults + stored overrides and gains.</summary>
    public KitSettings ToKitSettings() => new(DrumPieces.All.Select(GetPieceSettings).ToArray());

    /// <summary>Stores a piece's settings sparsely (values equal to the piece defaults are not written).</summary>
    public void SetPieceSettings(DrumPiece p, PieceSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var c = this[p];
        var diff = TriggerOverrides.Diff(s.Trigger.Normalized(), KitDefaults.TriggerFor(p).Normalized());
        c.Trigger = diff.IsEmpty ? null : diff;
        c.OutputGainDb = MathF.Abs(s.OutputGainDb) < 1e-4f ? null : s.OutputGainDb;
    }

    /// <summary>Replaces the profile with the same name (or adds it).</summary>
    public void SetProfile(RoutingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int i = Profiles.FindIndex(x => x.Name == profile.Name);
        if (i >= 0) Profiles[i] = profile; else Profiles.Add(profile);
    }

    /// <summary>Restores a built-in profile (Igreja / Simulação) to its original routes.</summary>
    public void ResetProfile(string name)
    {
        var builtIn = RoutingProfiles.BuiltIn.FirstOrDefault(x => x.Name == name)
            ?? throw new ArgumentException($"Perfil \"{name}\" não é um perfil de fábrica.", nameof(name));
        SetProfile(builtIn);
    }

    /// <summary>Current selection as a preset (sample, gain and detection overrides of each piece).</summary>
    public KitPreset ToPreset(string name) => new()
    {
        Name = name,
        Pieces = DrumPieces.All.ToDictionary(p => p, p =>
        {
            var c = this[p];
            return new PiecePreset(c.SamplePath, c.OutputGainDb ?? 0f, c.Trigger);
        }),
    };

    /// <summary>
    /// Stores a preset's selection in the config: sample per piece (missing = none), gain and detection when the
    /// preset defines them (otherwise kept).
    /// </summary>
    public void ApplyPreset(KitPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        foreach (var p in DrumPieces.All)
        {
            var c = this[p];
            var pp = preset[p];
            c.SamplePath = pp?.SamplePath;
            if (pp?.GainDb is { } g) c.OutputGainDb = MathF.Abs(g) < 1e-4f ? null : g;
            if (pp?.Detection is { } d)
            {
                var diff = TriggerOverrides.Diff(d.ApplyTo(KitDefaults.TriggerFor(p)), KitDefaults.TriggerFor(p).Normalized());
                c.Trigger = diff.IsEmpty ? null : diff;
            }
        }
    }

    /// <summary>Ensures 4 pieces (in order), the built-in profiles, 4 routes per profile and a valid active profile.</summary>
    public void Normalize()
    {
        SettingsVersion = CurrentVersion;
        var pieces = new List<PieceConfig>();
        foreach (var p in DrumPieces.All)
        {
            var c = Pieces?.FirstOrDefault(x => x is not null && x.Piece == p) ?? new PieceConfig { Piece = p };
            if (c.Trigger is { IsEmpty: true }) c.Trigger = null;
            pieces.Add(c);
        }
        Pieces = pieces;

        var profiles = new List<RoutingProfile>();
        foreach (var prof in Profiles ?? [])
        {
            if (prof is null || string.IsNullOrWhiteSpace(prof.Name) || profiles.Any(x => x.Name == prof.Name)) continue;
            profiles.Add(prof with
            {
                Routes = DrumPieces.All.Select(p => prof[p]).ToArray(),
                Reserved = prof.Reserved?.Where(r => r is not null).ToArray() ?? [],
            });
        }
        foreach (var b in RoutingProfiles.BuiltIn)
            if (!profiles.Any(x => x.Name == b.Name)) profiles.Add(b);
        Profiles = profiles;

        if (!Profiles.Any(x => x.Name == ActiveProfileName)) ActiveProfileName = RoutingProfiles.ChurchName;
    }

    public static AppConfig Load(out string? message) => Load(DefaultFilePath, out message);

    /// <summary>
    /// Loads <paramref name="path"/>. Missing → defaults (no message). Corrupt → defaults + pt-BR message, the bad file
    /// copied to settings.invalido.json. v1 → migrated and saved as v2, the original copied to settings.v1.json
    /// (an existing backup is never overwritten), pt-BR message.
    /// </summary>
    public static AppConfig Load(string path, out string? message)
    {
        message = null;
        if (!File.Exists(path)) return CreateDefault();
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = $"AVISO: não foi possível ler {path} ({ex.Message}); usando valores padrão.";
            return CreateDefault();
        }

        AppConfig cfg;
        bool wasV1;
        try
        {
            wasV1 = IsV1(json);
            cfg = Parse(json, out message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            string backup = Path.Combine(dir, InvalidBackupFileName);
            try { File.Copy(path, backup, overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            message = $"AVISO: {path} está corrompido ({ex.Message}); usando valores padrão. Cópia do arquivo em {backup}.";
            return CreateDefault();
        }

        if (wasV1)
        {
            string backup = Path.Combine(dir, V1BackupFileName);
            try
            {
                if (!File.Exists(backup)) File.Copy(path, backup);
                cfg.Save(path);
                message += $" Cópia da configuração antiga em {backup}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += $" AVISO: não foi possível gravar a cópia/configuração nova ({ex.Message}).";
            }
        }
        return cfg;
    }

    /// <summary>Defaults (normalized).</summary>
    public static AppConfig CreateDefault()
    {
        var c = new AppConfig();
        c.Normalize();
        return c;
    }

    /// <summary>Parses JSON without file I/O (v1 JSON is migrated). Throws <see cref="JsonException"/> on invalid JSON.</summary>
    public static AppConfig Parse(string json, out string? message)
    {
        message = null;
        if (IsV1(json))
        {
            var migrated = MigrateFromV1(json, out var note);
            message = note;
            return migrated;
        }
        var parsed = JsonSerializer.Deserialize<AppConfig>(json, KitPreset.JsonOptions)
            ?? throw new JsonException("Arquivo de configuração vazio.");
        if (parsed.SettingsVersion > CurrentVersion)
            message = $"AVISO: configuração de uma versão mais nova ({parsed.SettingsVersion}); campos desconhecidos foram ignorados.";
        parsed.Normalize();
        return parsed;
    }

    /// <summary>True for a JSON object without SettingsVersion or with SettingsVersion &lt; 2 (v1 app files).</summary>
    private static bool IsV1(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A configuração não é um objeto JSON.");
        return !doc.RootElement.TryGetProperty(nameof(SettingsVersion), out var v)
               || v.ValueKind != JsonValueKind.Number || v.GetInt32() < CurrentVersion;
    }

    /// <summary>
    /// Migrates a v1 settings.json (src/SampleDrum.App/AppSettings.cs) into a v2 config: driver/simulation, kick
    /// sample, kick detection values and the church profile's kick route. Other pieces stay at their defaults
    /// (church routes pending).
    /// </summary>
    public static AppConfig MigrateFromV1(string v1Json, out string note)
    {
        var v1 = JsonSerializer.Deserialize<V1Settings>(v1Json, KitPreset.JsonOptions) ?? new V1Settings();
        var cfg = CreateDefault();
        cfg.DriverName = v1.DriverName;
        cfg.UseSimulation = v1.UseSimulation ?? true;

        // v0 files (no SettingsVersion) always wrote threshold/rearm/interval: the old defaults mean "never calibrated".
        bool v0 = (v1.SettingsVersion ?? 0) < 1;
        float? thr = v0 && v1.ThresholdDb == -30f ? null : v1.ThresholdDb;
        float? rearm = v0 && v1.RearmDb == -40f ? null : v1.RearmDb;
        float? interval = v0 && v1.MinIntervalMs == 50f ? null : v1.MinIntervalMs;

        var kickDefaults = KitDefaults.TriggerFor(DrumPiece.Kick);
        var overrides = new TriggerOverrides
        {
            ThresholdDb = thr, RearmDb = rearm, MinIntervalMs = interval,
            HighPassHz = v1.HighPassHz, DetectTimeMs = v1.DetectTimeMs, ScanMs = v1.ScanMs, RearmTimeMs = v1.RearmTimeMs,
            RetriggerRiseDb = v1.RetriggerRiseDb, VelocityRangeDb = v1.VelocityRangeDb, MinGainDb = v1.MinGainDb,
            FixedVelocity = v1.FixedVelocity,
        };
        var kick = cfg[DrumPiece.Kick];
        kick.SamplePath = v1.SamplePath;
        kick.AdvancedExpanded = v1.AdvancedExpanded ?? false;
        var diff = TriggerOverrides.Diff(overrides.ApplyTo(kickDefaults), kickDefaults.Normalized());
        kick.Trigger = diff.IsEmpty ? null : diff;

        static int? Channel(int? c, int fallback) => c is >= 1 and <= ChannelMap.MaxChannels ? c : fallback;
        int inCh = Channel(v1.InputDeskChannel, 3)!.Value;
        int outCh = Channel(v1.OutputDeskChannel, 1)!.Value;
        cfg.SetProfile(cfg.Profiles.First(x => x.Name == RoutingProfiles.ChurchName)
            .WithRoute(DrumPiece.Kick, new PieceRoute(inCh, outCh)));
        cfg.ActiveProfileName = RoutingProfiles.ChurchName;
        cfg.Normalize();

        note = $"Configuração da versão 1 migrada: bumbo com entrada USB {inCh} / saída USB {outCh} no perfil " +
               $"\"{RoutingProfiles.ChurchName}\"{(kick.Trigger is null ? "" : ", ajustes de detecção mantidos")}" +
               $"{(kick.SamplePath is null ? "" : ", sample mantido")}; caixa, tom e surdo nos padrões (rotas pendentes).";
        return cfg;
    }

    public string ToJson() => JsonSerializer.Serialize(this, KitPreset.JsonOptions);

    public void Save() => Save(DefaultFilePath);

    /// <summary>Writes atomically (temp file + move).</summary>
    public void Save(string path)
    {
        Normalize();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Fields of the v1 app settings file (all optional).</summary>
    private sealed class V1Settings
    {
        public int? SettingsVersion { get; set; }
        public string? DriverName { get; set; }
        public bool? UseSimulation { get; set; }
        public int? InputDeskChannel { get; set; }
        public int? OutputDeskChannel { get; set; }
        public string? SamplePath { get; set; }
        public bool? AdvancedExpanded { get; set; }
        public float? ThresholdDb { get; set; }
        public float? RearmDb { get; set; }
        public float? MinIntervalMs { get; set; }
        public float? HighPassHz { get; set; }
        public float? DetectTimeMs { get; set; }
        public float? ScanMs { get; set; }
        public float? RearmTimeMs { get; set; }
        public float? RetriggerRiseDb { get; set; }
        public float? VelocityRangeDb { get; set; }
        public float? MinGainDb { get; set; }
        public bool? FixedVelocity { get; set; }
    }
}
