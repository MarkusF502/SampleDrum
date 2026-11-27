using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>Core support for the phone panel adapter: kit generation per operation and selection change versions.</summary>
public sealed class PanelRemoteSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-remote-core-" + Guid.NewGuid().ToString("N"));

    public PanelRemoteSupportTests()
    {
        Directory.CreateDirectory(_dir);
        WavFile.WriteMono(Path.Combine(_dir, "a.wav"), Enumerable.Repeat(0.25f, 16).ToArray(), Fs);
        WavFile.WriteMono(Path.Combine(_dir, "b.wav"), Enumerable.Repeat(0.75f, 16).ToArray(), Fs);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static Task<KitOperationSnapshot> Done(KitOperation op) => op.Completion.WaitAsync(TimeSpan.FromSeconds(15));

    private static KitPreset Preset(string id, string file) => new()
    {
        Id = id, Name = id, Pieces = DrumPieces.All.ToDictionary(p => p, _ => new PiecePreset(file)),
    };

    private static void OneBlock(KitEngine engine)
    {
        var io = DrumPieces.All.Select(_ => new float[64]).ToArray();
        var outs = DrumPieces.All.Select(_ => new float[64]).ToArray();
        engine.Process(io, outs, 64);
    }

    [Fact]
    public async Task AppliedKitSwap_RecordsItsGeneration_ConfirmedOnlyAfterTheAudioThreadTakesIt()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        var op = await Done(controller.SetPieceSample(DrumPiece.Kick, "a.wav"));
        Assert.Equal(KitOperationStatus.Applied, op.Status);
        Assert.NotNull(op.KitGeneration);
        Assert.Equal(engine.SubmittedKitGeneration, op.KitGeneration);
        Assert.True(engine.AppliedKitGeneration < op.KitGeneration);     // no block processed yet
        OneBlock(engine);
        Assert.True(engine.AppliedKitGeneration >= op.KitGeneration);
        Assert.Equal(op.KitGeneration, controller.GetOperation(op.Id)!.KitGeneration);

        var gain = await Done(controller.SetPieceGain(DrumPiece.Kick, -3));
        Assert.Null(gain.KitGeneration);                                  // no kit swap
        var failed = await Done(controller.SetPieceSample(DrumPiece.Kick, "missing.wav"));
        Assert.Equal(KitOperationStatus.Failed, failed.Status);
        Assert.Null(failed.KitGeneration);

        var preset = await Done(controller.ApplyPreset(Preset("p", "b.wav")));
        Assert.True(preset.KitGeneration > op.KitGeneration);
        Assert.Equal(preset.KitGeneration, engine.ApplyKit(engine.ActiveKit!) - 1);   // ApplyKit returns the next one
    }

    [Fact]
    public async Task SampleAndKitVersions_MoveOnlyWithTheSelection()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        var s0 = controller.Snapshot;
        Assert.Equal(0, s0.KitVersion);
        Assert.All(s0.Pieces, p => Assert.Equal(0, p.SampleVersion));

        await Done(controller.SetPieceSample(DrumPiece.Kick, "a.wav"));          // v1
        var s1 = controller.Snapshot;
        Assert.Equal(1, s1.Version);
        Assert.Equal(1, s1.Pieces[(int)DrumPiece.Kick].SampleVersion);
        Assert.Equal(0, s1.Pieces[(int)DrumPiece.Snare].SampleVersion);
        Assert.Equal(1, s1.KitVersion);

        await Done(controller.SetPieceGain(DrumPiece.Snare, -6));               // v2: not a selection change
        await Done(controller.SetPieceEnabled(DrumPiece.Tom, false));           // v3
        await Done(controller.SetKitEnabled(true));                             // v4
        var s4 = controller.Snapshot;
        Assert.Equal(4, s4.Version);
        Assert.Equal(1, s4.KitVersion);
        Assert.Equal(0, s4.Pieces[(int)DrumPiece.Snare].SampleVersion);

        await Done(controller.SetPieceSample(DrumPiece.Kick, "a.wav"));          // v5: same file again
        Assert.Equal(1, controller.Snapshot.Pieces[(int)DrumPiece.Kick].SampleVersion);
        Assert.Equal(1, controller.Snapshot.KitVersion);

        await Done(controller.ApplyPreset(Preset("p", "a.wav")));               // v6: 3 pieces change + preset id
        var s6 = controller.Snapshot;
        Assert.Equal(6, s6.Version);
        Assert.Equal(1, s6.Pieces[(int)DrumPiece.Kick].SampleVersion);           // kick already had a.wav
        Assert.Equal(6, s6.Pieces[(int)DrumPiece.Snare].SampleVersion);
        Assert.Equal(6, s6.KitVersion);

        await Done(controller.ApplyPreset(Preset("p", "a.wav")));               // v7: identical re-apply
        Assert.Equal(6, controller.Snapshot.KitVersion);

        await Done(controller.SetPieceSample(DrumPiece.Snare, "b.wav"));         // v8: preset id cleared too
        var s8 = controller.Snapshot;
        Assert.Null(s8.ActivePresetId);
        Assert.Equal(8, s8.Pieces[(int)DrumPiece.Snare].SampleVersion);
        Assert.Equal(6, s8.Pieces[(int)DrumPiece.Tom].SampleVersion);
        Assert.Equal(8, s8.KitVersion);

        await Done(controller.SetPieceSample(DrumPiece.Snare, null));            // v9: removing is a change
        Assert.Equal(9, controller.Snapshot.Pieces[(int)DrumPiece.Snare].SampleVersion);
    }

    [Fact]
    public async Task ApplySnapshot_UpdatesTheInMemoryConfig_SoLaterSavesKeepTheRemoteChange()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        await Done(controller.ApplyPreset(Preset("p", "a.wav")));
        await Done(controller.SetPieceSample(DrumPiece.Snare, "b.wav"));        // e.g. from the phone
        await Done(controller.SetPieceEnabled(DrumPiece.Tom, false));
        var cfg = AppConfig.CreateDefault();
        cfg.SamplesFolder = _dir;
        cfg.ApplySnapshot(controller.Snapshot);
        Assert.Null(cfg.ActivePresetId);
        Assert.Equal(Path.Combine(_dir, "b.wav"), cfg[DrumPiece.Snare].SamplePath);
        Assert.Equal(Path.Combine(_dir, "a.wav"), cfg[DrumPiece.Kick].SamplePath);
        Assert.False(cfg[DrumPiece.Tom].Enabled);
        string file = Path.Combine(_dir, "settings.json");
        cfg.Save(file);                                                         // unrelated save later
        var back = AppConfig.Load(file, out _);
        Assert.Equal(Path.Combine(_dir, "b.wav"), back[DrumPiece.Snare].SamplePath);
        Assert.DoesNotContain("KitEnabled", File.ReadAllText(file));
    }

    [Fact]
    public async Task UnnamedKits_CompareByBuffer()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        var kit = KitTestUtil.DcKit(32, 0.5f, 0.5f, 0.5f, 0.5f);
        await Done(controller.ApplyKit(kit));                                   // v1
        Assert.Equal(1, controller.Snapshot.KitVersion);
        await Done(controller.ApplyKit(kit));                                   // v2: same buffers
        Assert.Equal(1, controller.Snapshot.KitVersion);
        await Done(controller.ApplyKit(KitTestUtil.DcKit(32, 0.5f, 0.5f, 0.5f, 0.5f)));   // v3: new buffers
        Assert.Equal(3, controller.Snapshot.KitVersion);
    }
}
