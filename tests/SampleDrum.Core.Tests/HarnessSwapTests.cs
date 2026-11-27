using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.Core.Tests;

/// <summary>
/// Simulation demo library and swap verification (<see cref="SwapHarness"/>, same code as
/// <c>SampleDrum.App.exe --harness --swap</c>).
/// </summary>
public sealed class HarnessSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sd-swap-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SimulationSampleLibrary _lib;

    public HarnessSwapTests() => _lib = SimulationSampleLibrary.Create(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void DemoLibrary_HasThreeRecognizableVariantsPerPiece_AndLoadablePresets()
    {
        foreach (var p in DrumPieces.All)
            Assert.True(_lib.Samples.Count(s => s.Piece == p) >= 3, p.Label());
        Assert.Contains(_lib.Samples, s => s.Id == "bumbos/bumbo-seco.wav");
        Assert.Contains(_lib.Samples, s => s.Id == "caixas/caixa-aberta.wav");

        var listed = new SampleLibrary(_lib.SamplesFolder).List();
        Assert.Equal(_lib.Samples.Select(s => s.Id).OrderBy(x => x), listed.Select(s => s.Id).OrderBy(x => x));
        Assert.Contains(listed, s => s.Id == "caixas/caixa-aberta.wav" && s.Channels == 2);
        Assert.Contains(listed, s => s.Id == "tons/tom-agudo.wav" && s.SampleRate == 44100);

        var presets = KitPreset.LoadFolder(_lib.PresetsFolder, out var errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "demo-com-erro", "demo-rock", "demo-suave" }, presets.Select(p => p.Id).OrderBy(x => x));
        Assert.All(_lib.Presets, p => Assert.False(Path.IsPathRooted(p[DrumPiece.Kick]!.SamplePath)));
    }

    [Fact]
    public async Task DemoPresets_PrepareOrFailWithPieceCode()
    {
        var library = new SampleLibrary(_lib.SamplesFolder);
        foreach (var id in new[] { "demo-rock", "demo-suave" })
        {
            var r = await library.PrepareAsync(KitPreset.Load(Path.Combine(_lib.PresetsFolder, id + ".json")), 48000);
            Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
            Assert.All(DrumPieces.All, p => Assert.NotNull(r.Kit![p].Samples));
        }
        var bad = await library.PrepareAsync(_lib.Preset(SimulationSampleLibrary.BrokenPresetId), 48000);
        Assert.False(bad.Success);
        var err = Assert.Single(bad.Errors);
        Assert.Equal(DrumPiece.Snare, err.Piece);
        Assert.Equal(PieceLoadErrorCode.FileNotFound, err.Code);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(480)]
    public async Task SwapRun_Offline_AllChecksPass(int block)
    {
        var o = new SwapOptions { BlockSize = block };
        var rec = await SwapHarness.RunOfflineAsync(_lib, o);
        var rep = SwapHarness.Analyze(rec, o);
        Assert.True(rep.Passed, string.Join("\n", rep.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.Equal(3, rec.Operations.Count);
        Assert.True(rep.Voices.Count >= 30);
        // Each phase is represented by real hits.
        Assert.Contains(rep.Voices, v => v.Phase == "depois: peça");
        Assert.Contains(rep.Voices, v => v.Phase == "depois: preset");
    }

    [Fact]
    public async Task Analyzer_DetectsAVoiceCutAtTheSwap()
    {
        var o = new SwapOptions();
        var rec = await SwapHarness.RunOfflineAsync(_lib, o);
        var single = rec.Operations.Single(op => op.Step == "peça");
        // Cut the ringing kick voice at the swap block (what a naive swap that stops old voices would do).
        long swapBlock = rec.BlockKits.Select((k, i) => (k, i)).First(x => ReferenceEquals(x.k, single.KitAfter)).i;
        var kick = (float[])rec.ByPiece[0].Clone();
        for (long i = swapBlock * o.BlockSize; i < swapBlock * o.BlockSize + 4800; i++) kick[i] = 0f;
        var tampered = Clone(rec, byPiece: [kick, rec.ByPiece[1], rec.ByPiece[2], rec.ByPiece[3]]);
        var rep = SwapHarness.Analyze(tampered, o);
        Assert.False(rep.Passed);
        Assert.Contains(rep.Checks, c => !c.Passed && c.Name.StartsWith("Nenhuma voz cortada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyzer_DetectsAudioThatDoesNotMatchTheReportedKit()
    {
        var o = new SwapOptions();
        var rec = await SwapHarness.RunOfflineAsync(_lib, o);
        var preset = rec.Operations.Single(op => op.Step == "preset");
        // Claim the preset kit played from the very first block: the audio of the early hits contradicts it.
        var kits = rec.BlockKits.Select(_ => preset.KitAfter).ToList();
        var rep = SwapHarness.Analyze(Clone(rec, blockKits: kits), o);
        Assert.False(rep.Passed);
        Assert.Contains(rep.Voices, v => !v.Ok);
    }

    private static SwapRecording Clone(SwapRecording r, float[][]? byPiece = null, IReadOnlyList<PreparedKit?>? blockKits = null) => new()
    {
        Scenario = r.Scenario, Mode = r.Mode, BlockSize = r.BlockSize, ByPiece = byPiece ?? r.ByPiece, Events = r.Events,
        BlockKits = blockKits ?? r.BlockKits, BlockGenerations = r.BlockGenerations, Operations = r.Operations, Callbacks = r.Callbacks,
    };
}
