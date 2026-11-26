using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>AppConfig additions for the phone panel and preset/sample identity.</summary>
public sealed class PanelConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-panelcfg-" + Guid.NewGuid().ToString("N"));

    public PanelConfigTests() => Directory.CreateDirectory(Path.Combine(_dir, "samples", "kicks"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RemotePanel_DefaultsOff_AndRoundTrips()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.False(cfg.RemotePanel.Enabled);
        Assert.False(cfg.RemotePanel.AllowPhoneTest);
        Assert.Equal(8750, cfg.RemotePanel.Port);
        Assert.Empty(cfg.RemotePanel.PairedDevices);

        cfg.RemotePanel.Enabled = true;
        cfg.RemotePanel.Port = 9001;
        cfg.RemotePanel.PairedDevices.Add(new PairedDeviceConfig
        {
            Name = "Celular do som", TokenSha256Hex = new string('a', 64),
            PairedAtUtc = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
        });
        cfg.ActivePresetId = "domingo";
        var back = AppConfig.Parse(cfg.ToJson(), out var msg);
        Assert.Null(msg);
        Assert.True(back.RemotePanel.Enabled);
        Assert.Equal(9001, back.RemotePanel.Port);
        var dev = Assert.Single(back.RemotePanel.PairedDevices);
        Assert.Equal("Celular do som", dev.Name);
        Assert.Null(dev.LastSeenUtc);
        Assert.Equal(cfg.RemotePanel.PairedDevices[0].PairedAtUtc, dev.PairedAtUtc);
        Assert.Equal("domingo", back.ActivePresetId);
        Assert.DoesNotContain("KitEnabled", cfg.ToJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(70000)]
    public void RemotePanel_InvalidPort_ResetToDefault(int port)
    {
        var cfg = AppConfig.Parse($$"""{ "SettingsVersion": 2, "RemotePanel": { "Port": {{port}} } }""", out _);
        Assert.Equal(RemotePanelConfig.DefaultPort, cfg.RemotePanel.Port);
    }

    [Fact]
    public void RemotePanel_DeviceWithoutTokenHash_IsDropped()
    {
        var cfg = AppConfig.Parse("""{ "SettingsVersion": 2, "RemotePanel": { "PairedDevices": [ { "Name": "x" }, { "Name": "y", "TokenSha256Hex": "ab" } ] } }""", out _);
        Assert.Equal("y", Assert.Single(cfg.RemotePanel.PairedDevices).Name);
    }

    [Fact]
    public void V1Migration_GetsPanelDefaults()
    {
        var cfg = AppConfig.Parse("""{ "SettingsVersion": 1, "InputDeskChannel": 3, "OutputDeskChannel": 1 }""", out _);
        Assert.False(cfg.RemotePanel.Enabled);
        Assert.Null(cfg.ActivePresetId);
    }

    [Fact]
    public void SamplePath_InsideSamplesFolder_StoredRelative_ResolvedAbsoluteOnLoad()
    {
        string samples = Path.Combine(_dir, "samples");
        string inside = Path.Combine(samples, "kicks", "k1.wav");
        string outside = Path.Combine(_dir, "fora.wav");
        var cfg = AppConfig.CreateDefault();
        cfg.SamplesFolder = samples;
        cfg[DrumPiece.Kick].SamplePath = inside;
        cfg[DrumPiece.Snare].SamplePath = outside;

        string json = cfg.ToJson();
        Assert.Contains("\"kicks/k1.wav\"", json);
        Assert.Contains(Path.GetFileName(outside), json);
        Assert.Equal(inside, cfg[DrumPiece.Kick].SamplePath);     // ToJson works on a copy

        var back = AppConfig.Parse(json, out _);
        Assert.Equal(Path.GetFullPath(inside), back[DrumPiece.Kick].SamplePath);
        Assert.Equal(Path.GetFullPath(outside), back[DrumPiece.Snare].SamplePath);
    }

    [Fact]
    public void ApplyPreset_SetsActivePresetId_FromFileIdentity()
    {
        string presets = Path.Combine(_dir, "presets");
        new KitPreset { Name = "Domingo" }.Save(Path.Combine(presets, "domingo-manha.json"));
        var preset = KitPreset.Load(Path.Combine(presets, "domingo-manha.json"));
        Assert.DoesNotContain("\"Id\"", File.ReadAllText(Path.Combine(presets, "domingo-manha.json")));
        var cfg = AppConfig.CreateDefault();
        cfg.ApplyPreset(preset);
        Assert.Equal("domingo-manha", cfg.ActivePresetId);
    }

    [Fact]
    public void SaveFromSnapshot_WritesSnapshotAtomically_WithoutMutatingTheConfig()
    {
        string path = Path.Combine(_dir, "settings.json");
        var cfg = AppConfig.CreateDefault();
        var settings = PieceSettings.DefaultFor(DrumPiece.Tom) with { OutputGainDb = -5f };
        var snapshot = new KitStateSnapshot(7, KitEnabled: true, ActivePresetId: "p1", DrumPieces.All.Select(p =>
            new KitPieceSnapshot(p, null, p == DrumPiece.Tom ? Path.Combine(_dir, "t.wav") : null,
                p == DrumPiece.Tom ? -5f : 0f, Enabled: p != DrumPiece.Snare,
                p == DrumPiece.Tom ? settings : PieceSettings.DefaultFor(p))).ToArray());

        cfg.Save(path, snapshot);
        Assert.Null(cfg.ActivePresetId);                          // the in-memory config is untouched
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));          // temp file moved into place
        var back = AppConfig.Load(path, out var msg);
        Assert.Null(msg);
        Assert.Equal("p1", back.ActivePresetId);
        Assert.False(back[DrumPiece.Snare].Enabled);
        Assert.Equal(-5f, back.GetPieceSettings(DrumPiece.Tom).OutputGainDb);
        Assert.Equal(Path.Combine(_dir, "t.wav"), back[DrumPiece.Tom].SamplePath);
        Assert.DoesNotContain("KitEnabled", File.ReadAllText(path));
    }

    [Fact]
    public async Task PreparePieceAsync_ClearsPresetId_CustomSelection()
    {
        string samples = Path.Combine(_dir, "samples");
        WavFile.WriteMono(Path.Combine(samples, "a.wav"), new float[] { 0.5f, 0.5f }, Fs);
        var lib = new SampleLibrary(samples);
        var preset = new KitPreset { Id = "p", Pieces = new Dictionary<DrumPiece, PiecePreset> { [DrumPiece.Kick] = new("a.wav") } };
        var kit = (await lib.PrepareAsync(preset, Fs)).Kit!;
        Assert.Equal("p", kit.PresetId);
        var changed = (await lib.PreparePieceAsync(kit, DrumPiece.Snare, "a.wav")).Kit!;
        Assert.Null(changed.PresetId);
    }
}
