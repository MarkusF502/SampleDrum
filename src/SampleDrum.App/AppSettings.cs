using System.Text.Json;

namespace SampleDrum.App;

/// <summary>
/// Persisted UI settings: %AppData%\SampleDrum\settings.json. Channels are stored in desk numbering (1-based).
/// Automatic triggering is intentionally NOT persisted: the app always starts disabled.
/// </summary>
public sealed class AppSettings
{
    /// <summary>ASIO driver name, or null for the simulation host.</summary>
    public string? DriverName { get; set; }
    public bool UseSimulation { get; set; } = true;
    public int InputDeskChannel { get; set; } = 3;   // USB 3 -> ASIO index 2
    public int OutputDeskChannel { get; set; } = 1;  // USB 1 -> ASIO index 0
    public string? SamplePath { get; set; }
    public float ThresholdDb { get; set; } = -30f;
    public float RearmDb { get; set; } = -40f;
    public float MinIntervalMs { get; set; } = 50f;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SampleDrum", "settings.json");

    /// <summary>Loads settings; returns defaults (and a pt-BR warning) if missing or unreadable.</summary>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            warning = $"Não foi possível ler {FilePath} ({ex.Message}); usando valores padrão.";
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
