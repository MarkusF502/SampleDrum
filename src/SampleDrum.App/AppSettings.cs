using System.Text.Json;
using SampleDrum.Core;

namespace SampleDrum.App;

/// <summary>
/// Persisted UI settings: %AppData%\SampleDrum\settings.json. Channels are stored in desk numbering (1-based).
/// Trigger values are nullable: a value that is absent uses <see cref="TriggerSettings.Default"/>, so defaults always
/// come from Core and old files keep loading. Automatic triggering is intentionally NOT persisted (always starts disabled).
/// </summary>
public sealed class AppSettings
{
    /// <summary>1 = trigger values nullable/sparse (Core defaults). Missing/0 = file written by the first app version.</summary>
    public const int CurrentVersion = 1;

    public int SettingsVersion { get; set; }

    /// <summary>ASIO driver name (kept even while the simulation is selected).</summary>
    public string? DriverName { get; set; }
    public bool UseSimulation { get; set; } = true;
    public int InputDeskChannel { get; set; } = 3;   // USB 3 -> ASIO index 2
    public int OutputDeskChannel { get; set; } = 1;  // USB 1 -> ASIO index 0
    public string? SamplePath { get; set; }
    public bool AdvancedExpanded { get; set; }

    // Basic trigger controls.
    public float? ThresholdDb { get; set; }
    public float? RearmDb { get; set; }
    public float? MinIntervalMs { get; set; }

    // Advanced (calibration at the rehearsal).
    public float? HighPassHz { get; set; }
    public float? DetectTimeMs { get; set; }
    public float? RearmTimeMs { get; set; }
    public float? RetriggerRiseDb { get; set; }
    public float? VelocityRangeDb { get; set; }
    public float? MinGainDb { get; set; }
    public bool? FixedVelocity { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SampleDrum", "settings.json");

    /// <summary>Trigger settings = Core defaults overridden by the stored values, normalized.</summary>
    public TriggerSettings ToTriggerSettings()
    {
        var d = TriggerSettings.Default;
        return (d with
        {
            ThresholdDb = ThresholdDb ?? d.ThresholdDb,
            RearmDb = RearmDb ?? d.RearmDb,
            MinIntervalMs = MinIntervalMs ?? d.MinIntervalMs,
            HighPassHz = HighPassHz ?? d.HighPassHz,
            DetectTimeMs = DetectTimeMs ?? d.DetectTimeMs,
            RearmTimeMs = RearmTimeMs ?? d.RearmTimeMs,
            RetriggerRiseDb = RetriggerRiseDb ?? d.RetriggerRiseDb,
            VelocityRangeDb = VelocityRangeDb ?? d.VelocityRangeDb,
            MinGainDb = MinGainDb ?? d.MinGainDb,
            FixedVelocity = FixedVelocity ?? d.FixedVelocity,
        }).Normalized();
    }

    /// <summary>
    /// Stores the values the UI controls. Values equal to the Core default are stored as null (omitted), so a field
    /// the operator never changed keeps following <see cref="TriggerSettings.Default"/> if Core changes it later.
    /// </summary>
    public void FromTriggerSettings(TriggerSettings s)
    {
        var d = TriggerSettings.Default.Normalized();
        static float? Opt(float v, float def) => MathF.Abs(v - def) < 1e-4f ? null : v;
        ThresholdDb = Opt(s.ThresholdDb, d.ThresholdDb);
        RearmDb = Opt(s.RearmDb, d.RearmDb);
        MinIntervalMs = Opt(s.MinIntervalMs, d.MinIntervalMs);
        HighPassHz = Opt(s.HighPassHz, d.HighPassHz);
        DetectTimeMs = Opt(s.DetectTimeMs, d.DetectTimeMs);
        RearmTimeMs = Opt(s.RearmTimeMs, d.RearmTimeMs);
        RetriggerRiseDb = Opt(s.RetriggerRiseDb, d.RetriggerRiseDb);
        VelocityRangeDb = Opt(s.VelocityRangeDb, d.VelocityRangeDb);
        MinGainDb = Opt(s.MinGainDb, d.MinGainDb);
        FixedVelocity = s.FixedVelocity == d.FixedVelocity ? null : s.FixedVelocity;
    }

    /// <summary>Loads settings; returns defaults (and a pt-BR warning) if missing or unreadable.</summary>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings { SettingsVersion = CurrentVersion };
            return Parse(File.ReadAllText(FilePath), out warning);
        }
        catch (Exception ex)
        {
            warning = $"AVISO: não foi possível ler {FilePath} ({ex.Message}); usando valores padrão.";
            return new AppSettings { SettingsVersion = CurrentVersion };
        }
    }

    /// <summary>Parses settings JSON (unknown properties are ignored) and migrates older versions.</summary>
    internal static AppSettings Parse(string json, out string? note)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        note = s.Migrate();
        return s;
    }

    /// <summary>
    /// v0 files (first app version) always wrote ThresholdDb/RearmDb/MinIntervalMs, even when untouched. Values equal
    /// to the old defaults (-30 dB / -40 dB / 50 ms) are treated as "never calibrated" and replaced by the Core defaults;
    /// changed values are kept. Returns a pt-BR note when something was migrated.
    /// </summary>
    internal string? Migrate()
    {
        if (SettingsVersion >= CurrentVersion) return null;
        var reset = new List<string>();
        if (ThresholdDb == -30f) { ThresholdDb = null; reset.Add("limiar"); }
        if (RearmDb == -40f) { RearmDb = null; reset.Add("rearme"); }
        if (MinIntervalMs == 50f) { MinIntervalMs = null; reset.Add("intervalo mínimo"); }
        SettingsVersion = CurrentVersion;
        return reset.Count == 0
            ? "Configurações da versão anterior carregadas; valores ajustados foram mantidos."
            : $"Configurações da versão anterior: {string.Join(", ", reset)} ainda no padrão antigo, " +
              "trocados pelos novos padrões do detector.";
    }

    public void Save()
    {
        SettingsVersion = CurrentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
