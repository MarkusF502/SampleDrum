using SampleDrum.Core;
using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Tests;

public sealed class AppConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sd-cfg-{Guid.NewGuid():N}");

    public AppConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // Real shapes written by the v1 app (src/SampleDrum.App/AppSettings.cs, System.Text.Json, nulls omitted).
    private const string V1DefaultsOnly = """
        {
          "SettingsVersion": 1,
          "UseSimulation": true,
          "InputDeskChannel": 3,
          "OutputDeskChannel": 1,
          "AdvancedExpanded": false
        }
        """;

    private const string V1Calibrated = """
        {
          "SettingsVersion": 1,
          "DriverName": "X-USB ASIO Driver",
          "UseSimulation": false,
          "InputDeskChannel": 5,
          "OutputDeskChannel": 2,
          "SamplePath": "C:\\Samples\\kick-igreja.wav",
          "AdvancedExpanded": true,
          "ThresholdDb": -28,
          "RearmDb": -36,
          "MinIntervalMs": 40,
          "HighPassHz": 50,
          "ScanMs": 3,
          "VelocityRangeDb": 18,
          "FixedVelocity": true
        }
        """;

    // First app version (no SettingsVersion): always wrote the three basic fields, here at the OLD defaults.
    private const string V0OldDefaults = """
        { "UseSimulation": true, "InputDeskChannel": 3, "OutputDeskChannel": 1, "ThresholdDb": -30, "RearmDb": -40, "MinIntervalMs": 50 }
        """;

    [Fact]
    public void V1DefaultsOnly_MigratesToDefaults_KickRouteInChurchProfile()
    {
        var cfg = AppConfig.Parse(V1DefaultsOnly, out var msg);
        Assert.Equal(AppConfig.CurrentVersion, cfg.SettingsVersion);
        Assert.Contains("migrada", msg);
        Assert.True(cfg.UseSimulation);
        Assert.Equal(RoutingProfiles.ChurchName, cfg.ActiveProfileName);
        Assert.Equal(new PieceRoute(3, 1), cfg.ActiveProfile[DrumPiece.Kick]);
        foreach (var p in new[] { DrumPiece.Snare, DrumPiece.Tom, DrumPiece.FloorTom })
        {
            Assert.True(cfg.ActiveProfile[p].IsPending);
            Assert.Null(cfg[p].Trigger);
            Assert.Null(cfg[p].SamplePath);
        }
        Assert.Null(cfg[DrumPiece.Kick].Trigger);
        Assert.Equal(KitSettings.Default, cfg.ToKitSettings());
    }

    [Fact]
    public void V1Calibrated_KeepsKickCalibrationSampleDriverAndRoute()
    {
        var cfg = AppConfig.Parse(V1Calibrated, out var msg);
        Assert.Contains("ajustes de detecção mantidos", msg);
        Assert.Equal("X-USB ASIO Driver", cfg.DriverName);
        Assert.False(cfg.UseSimulation);
        Assert.Equal(new PieceRoute(5, 2), cfg.ActiveProfile[DrumPiece.Kick]);
        var kick = cfg[DrumPiece.Kick];
        Assert.Equal(@"C:\Samples\kick-igreja.wav", kick.SamplePath);
        Assert.True(kick.AdvancedExpanded);

        var t = cfg.GetPieceSettings(DrumPiece.Kick).Trigger;
        Assert.Equal(-28f, t.ThresholdDb);
        Assert.Equal(-36f, t.RearmDb);
        Assert.Equal(40f, t.MinIntervalMs);
        Assert.Equal(50f, t.HighPassHz);
        Assert.Equal(3f, t.ScanMs);
        Assert.Equal(18f, t.VelocityRangeDb);
        Assert.True(t.FixedVelocity);
        Assert.Equal(TriggerSettings.Default.DetectTimeMs, t.DetectTimeMs);   // absent in v1 -> default
        Assert.Equal(KitSettings.Default[DrumPiece.Snare], cfg.ToKitSettings()[DrumPiece.Snare]);
    }

    [Fact]
    public void V0FileWithOldDefaults_TreatedAsUncalibrated()
    {
        var cfg = AppConfig.Parse(V0OldDefaults, out _);
        Assert.Null(cfg[DrumPiece.Kick].Trigger);
        Assert.Equal(TriggerSettings.Default.ThresholdDb, cfg.GetPieceSettings(DrumPiece.Kick).Trigger.ThresholdDb);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"SamplePath\": \"k.wav\" }")]
    [InlineData("{ \"InputDeskChannel\": 99, \"OutputDeskChannel\": 0 }")]
    public void V1MissingOrInvalidFields_UseDefaults(string json)
    {
        var cfg = AppConfig.Parse(json, out var msg);
        Assert.NotNull(msg);
        Assert.Equal(new PieceRoute(3, 1), cfg.ActiveProfile[DrumPiece.Kick]);
        Assert.Equal(4, cfg.Pieces.Count);
        Assert.Equal(KitSettings.Default, cfg.ToKitSettings());
    }

    [Fact]
    public void Load_V1File_WritesBackupAndSavesV2()
    {
        string path = Path.Combine(_dir, AppConfig.FileName);
        File.WriteAllText(path, V1Calibrated);

        var cfg = AppConfig.Load(path, out var msg);
        Assert.Contains(AppConfig.V1BackupFileName, msg);
        string backup = Path.Combine(_dir, AppConfig.V1BackupFileName);
        Assert.Equal(V1Calibrated, File.ReadAllText(backup));
        Assert.Contains("\"SettingsVersion\": 2", File.ReadAllText(path));

        var again = AppConfig.Load(path, out var msg2);
        Assert.Null(msg2);                                              // already v2: no migration
        Assert.Equal(cfg.ToJson(), again.ToJson());

        // A later migration never overwrites the original backup.
        File.WriteAllText(path, V1DefaultsOnly);
        AppConfig.Load(path, out _);
        Assert.Equal(V1Calibrated, File.ReadAllText(backup));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"SettingsVersion\": 2, \"Pieces\": 5 }")]
    public void Load_CorruptFile_DefaultsMessageAndCopy(string content)
    {
        string path = Path.Combine(_dir, AppConfig.FileName);
        File.WriteAllText(path, content);
        var cfg = AppConfig.Load(path, out var msg);
        Assert.Contains("corrompido", msg);
        Assert.Equal(KitSettings.Default, cfg.ToKitSettings());
        Assert.Equal(content, File.ReadAllText(Path.Combine(_dir, AppConfig.InvalidBackupFileName)));
    }

    [Fact]
    public void Load_MissingFile_DefaultsWithoutMessage()
    {
        var cfg = AppConfig.Load(Path.Combine(_dir, "nao-existe.json"), out var msg);
        Assert.Null(msg);
        Assert.Equal(RoutingProfiles.ChurchName, cfg.ActiveProfileName);
    }

    [Fact]
    public void V2_RoundTrip_PreservesEverything_AndNeverPersistsKitEnabled()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.DriverName = "X-USB ASIO Driver";
        cfg.UseSimulation = false;
        cfg.SamplesFolder = @"D:\samples";
        cfg.SetPieceSettings(DrumPiece.Snare, PieceSettings.DefaultFor(DrumPiece.Snare) with
        {
            Trigger = KitDefaults.Snare with { ThresholdDb = -17f, MinIntervalMs = 20f },
            OutputGainDb = -4.5f,
        });
        cfg[DrumPiece.Tom].Enabled = false;
        cfg[DrumPiece.Tom].SamplePath = "toms/tom1.wav";
        cfg.SetProfile(cfg.ActiveProfile.WithRoute(DrumPiece.Snare, new PieceRoute(4, 2)));
        cfg.ActiveProfileName = RoutingProfiles.ChurchName;

        string json = cfg.ToJson();
        Assert.DoesNotContain("KitEnabled", json);
        Assert.DoesNotContain("IsPending", json);
        var back = AppConfig.Parse(json, out var msg);
        Assert.Null(msg);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(cfg.ToKitSettings(), back.ToKitSettings());
        Assert.Equal(-17f, back.GetPieceSettings(DrumPiece.Snare).Trigger.ThresholdDb);
        Assert.Equal(-4.5f, back.GetPieceSettings(DrumPiece.Snare).OutputGainDb);
        Assert.False(back[DrumPiece.Tom].Enabled);
        Assert.Equal(new PieceRoute(4, 2), back.ActiveProfile[DrumPiece.Snare]);
        Assert.Equal(RoutingProfiles.Simulation, back.Profiles.Single(p => p.Name == RoutingProfiles.SimulationName) with
        {
            Routes = RoutingProfiles.Simulation.Routes,
            Reserved = RoutingProfiles.Simulation.Reserved,
        });
    }

    [Fact]
    public void SparseStorage_DefaultValuesAreNotWritten()
    {
        var cfg = AppConfig.CreateDefault();
        foreach (var p in DrumPieces.All) cfg.SetPieceSettings(p, PieceSettings.DefaultFor(p));
        Assert.All(cfg.Pieces, c => Assert.Null(c.Trigger));
        Assert.All(cfg.Pieces, c => Assert.Null(c.OutputGainDb));
        Assert.DoesNotContain("ThresholdDb", cfg.ToJson());
    }

    [Fact]
    public void Normalize_RepairsMissingPiecesProfilesAndActiveProfile()
    {
        var cfg = AppConfig.Parse("""{ "SettingsVersion": 2, "ActiveProfileName": "Nenhum", "Pieces": [ { "Piece": "Tom", "Enabled": false } ], "Profiles": [] }""", out _);
        Assert.Equal(DrumPieces.All, cfg.Pieces.Select(p => p.Piece));
        Assert.False(cfg[DrumPiece.Tom].Enabled);
        Assert.Equal(RoutingProfiles.ChurchName, cfg.ActiveProfileName);
        Assert.Contains(cfg.Profiles, p => p.Name == RoutingProfiles.SimulationName);
    }

    [Fact]
    public void ResetProfile_RestoresBuiltIn()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.SetProfile(cfg.ActiveProfile.WithRoute(DrumPiece.Kick, new PieceRoute(7, 7)));
        cfg.ResetProfile(RoutingProfiles.ChurchName);
        Assert.Equal(new PieceRoute(3, 1), cfg.ActiveProfile[DrumPiece.Kick]);
    }

    [Fact]
    public void Preset_FromConfig_AndBack()
    {
        var cfg = AppConfig.CreateDefault();
        cfg[DrumPiece.Kick].SamplePath = "kick.wav";
        cfg.SetPieceSettings(DrumPiece.Kick, PieceSettings.DefaultFor(DrumPiece.Kick) with { OutputGainDb = -2f });
        var preset = cfg.ToPreset("Domingo");

        var other = AppConfig.CreateDefault();
        other.ApplyPreset(preset);
        Assert.Equal("kick.wav", other[DrumPiece.Kick].SamplePath);
        Assert.Equal(-2f, other.GetPieceSettings(DrumPiece.Kick).OutputGainDb);
        Assert.Null(other[DrumPiece.Snare].SamplePath);
    }
}
