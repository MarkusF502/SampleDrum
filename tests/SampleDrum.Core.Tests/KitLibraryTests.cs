using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

public sealed class KitLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sd-lib-{Guid.NewGuid():N}");

    public KitLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "toms"));
        WavFile.WriteMono(Path.Combine(_dir, "kick.wav"), Enumerable.Repeat(0.5f, 4800).ToArray(), 48000, WavSampleFormat.Pcm24);
        WavFile.WriteMono(Path.Combine(_dir, "snare44k.WAV"), Enumerable.Repeat(0.25f, 4410).ToArray(), 44100, WavSampleFormat.Pcm16);
        WavFile.WriteMono(Path.Combine(_dir, "toms", "tom1.wav"), Enumerable.Repeat(0.125f, 960).ToArray(), 48000, WavSampleFormat.Float32);
        File.WriteAllText(Path.Combine(_dir, "lixo.wav"), "isto não é um wav");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static KitPreset Preset(params (DrumPiece P, string? Path)[] pieces) => new()
    {
        Name = "teste",
        Pieces = pieces.ToDictionary(x => x.P, x => new PiecePreset(x.Path)),
    };

    [Fact]
    public void List_FindsWavsRecursively_SkipsInvalid_WithInfo()
    {
        var list = new SampleLibrary(_dir).List();
        Assert.Equal(new[] { "kick", "snare44k", "tom1" }, list.Select(s => s.Name));
        var snare = list.Single(s => s.Name == "snare44k");
        Assert.Equal(44100, snare.SampleRate);
        Assert.Equal(1, snare.Channels);
        Assert.Equal(0.1, snare.DurationSeconds, 3);
        Assert.Empty(new SampleLibrary(Path.Combine(_dir, "nao-existe")).List());
    }

    [Fact]
    public async Task PrepareAsync_LoadsResamplesAndKeepsPresetGainAndDetection()
    {
        var lib = new SampleLibrary(_dir);
        var preset = new KitPreset
        {
            Name = "Domingo",
            Pieces = new Dictionary<DrumPiece, PiecePreset>
            {
                [DrumPiece.Kick] = new("kick.wav", -2f),
                [DrumPiece.Snare] = new(Path.Combine(_dir, "snare44k.WAV"), null, new TriggerOverrides { ThresholdDb = -15f }),
                [DrumPiece.Tom] = new("toms/tom1.wav"),
            },
        };
        var r = await lib.PrepareAsync(preset, 48000);
        Assert.True(r.Success);
        var kit = r.Kit!;
        Assert.Equal("Domingo", kit.Name);
        Assert.Equal(4800, kit[DrumPiece.Kick].Samples!.Length);
        Assert.Equal(4800, kit[DrumPiece.Snare].Samples!.Length);           // 4410 @ 44.1k -> 4800 @ 48k
        Assert.Contains(r.Notes, n => n.Contains("Caixa") && n.Contains("44100"));
        Assert.Equal(960, kit[DrumPiece.Tom].Samples!.Length);
        Assert.Null(kit[DrumPiece.FloorTom].Samples);                         // not in the preset: no sample
        Assert.Equal(-2f, kit[DrumPiece.Kick].GainDb);
        Assert.Equal(-15f, kit[DrumPiece.Snare].Detection!.ThresholdDb);
    }

    [Fact]
    public async Task FailedPrepare_ReportsPerPieceErrors_AndLeavesActiveKitUntouched()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        var lib = new SampleLibrary(_dir);
        var good = await lib.PrepareAsync(Preset((DrumPiece.Kick, "kick.wav")), Fs);
        engine.ApplyKit(good.Kit!);
        var active = engine.ActiveKit;

        var bad = await lib.PrepareAsync(Preset((DrumPiece.Kick, "kick.wav"), (DrumPiece.Snare, "nao-existe.wav"),
            (DrumPiece.Tom, "lixo.wav")), Fs);
        Assert.False(bad.Success);
        Assert.Null(bad.Kit);
        Assert.Equal(new[] { DrumPiece.Snare, DrumPiece.Tom }, bad.Errors.Select(e => e.Piece));
        Assert.Contains("não encontrado", bad.Errors[0].Message);
        Assert.StartsWith("Tom:", bad.Errors[1].Message);

        // The caller only applies a successful kit: the engine keeps the previous one.
        if (bad.Success) engine.ApplyKit(bad.Kit!);
        Assert.Same(active, engine.ActiveKit);
    }

    [Fact]
    public async Task PrepareAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SampleLibrary(_dir).PrepareAsync(Preset((DrumPiece.Kick, "kick.wav")), Fs, cts.Token));
    }

    [Fact]
    public async Task PreparePieceAsync_ReplacesOnePiece_KeepsOthers_DropsGainAndDetection()
    {
        var lib = new SampleLibrary(_dir);
        var preset = new KitPreset
        {
            Name = "k",
            Pieces = new Dictionary<DrumPiece, PiecePreset>
            {
                [DrumPiece.Kick] = new("kick.wav", -6f),
                [DrumPiece.Tom] = new("toms/tom1.wav"),
            },
        };
        var current = (await lib.PrepareAsync(preset, Fs)).Kit!;

        var r = await lib.PreparePieceAsync(current, DrumPiece.Snare, "snare44k.WAV");
        Assert.True(r.Success);
        Assert.Same(current[DrumPiece.Kick].Samples, r.Kit![DrumPiece.Kick].Samples);
        Assert.Same(current[DrumPiece.Tom].Samples, r.Kit[DrumPiece.Tom].Samples);
        Assert.NotNull(r.Kit[DrumPiece.Snare].Samples);
        Assert.All(r.Kit.Pieces, p => Assert.Null(p.GainDb));                // re-applying never reverts gains

        var removed = await lib.PreparePieceAsync(r.Kit, DrumPiece.Kick, null);
        Assert.Null(removed.Kit![DrumPiece.Kick].Samples);

        var failed = await lib.PreparePieceAsync(r.Kit, DrumPiece.Tom, "nao-existe.wav");
        Assert.Null(failed.Kit);
        Assert.Single(failed.Errors);
    }

    [Fact]
    public async Task SinglePieceChange_DuringPlayback_OnlyThatPieceChangesFromNextHit()
    {
        var lib = new SampleLibrary(_dir);
        var engine = KitTestUtil.Engine(KitTestUtil.AllPieces(Impulsive with { MinIntervalMs = 0f }),
            (await lib.PrepareAsync(Preset((DrumPiece.Kick, "kick.wav"), (DrumPiece.Tom, "toms/tom1.wav")), Fs)).Kit!);
        var swapped = (await lib.PreparePieceAsync(engine.ActiveKit!, DrumPiece.Kick, "toms/tom1.wav")).Kit!;
        engine.ApplyKit(swapped);

        var inB = KitTestUtil.Mics(128.0 / Fs);
        var outB = KitTestUtil.Mics(128.0 / Fs);
        inB[0][0] = 0.5f;
        inB[2][0] = 0.5f;
        engine.Process(inB, outB, 128);
        Assert.Equal(0.125f, outB[0][0], 6);                                  // kick now plays tom1.wav
        Assert.Equal(0.125f, outB[2][0], 6);                                  // tom unchanged
    }

    [Fact]
    public void Preset_SaveLoadRoundTrip_AndFolderErrors()
    {
        var preset = new KitPreset
        {
            Name = "Culto de domingo",
            Pieces = new Dictionary<DrumPiece, PiecePreset>
            {
                [DrumPiece.Kick] = new("kick.wav", -1.5f, new TriggerOverrides { ThresholdDb = -26f, FixedVelocity = true }),
                [DrumPiece.FloorTom] = new("toms/tom1.wav"),
            },
        };
        string folder = Path.Combine(_dir, "presets");
        string path = Path.Combine(folder, KitPreset.FileNameFor("Culto: domingo?"));
        preset.Save(path);
        Assert.Contains("\"Kick\"", File.ReadAllText(path));                  // enum keys as names

        var back = KitPreset.Load(path);
        Assert.Equal(preset.Name, back.Name);
        Assert.Equal(preset[DrumPiece.Kick], back[DrumPiece.Kick]);
        Assert.Equal(preset[DrumPiece.FloorTom], back[DrumPiece.FloorTom]);
        Assert.Null(back[DrumPiece.Snare]);

        File.WriteAllText(Path.Combine(folder, "quebrado.json"), "{ nada");
        var all = KitPreset.LoadFolder(folder, out var errors);
        Assert.Single(all);
        var err = Assert.Single(errors);
        Assert.StartsWith("quebrado.json", err);
    }

    [Fact]
    public void Preset_NewerVersionOrBadPiece_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => KitPreset.FromJson("""{ "PresetVersion": 99, "Name": "x" }"""));
        Assert.Throws<InvalidDataException>(() => KitPreset.FromJson("""{ "Name": "x", "Pieces": { "Cowbell": {} } }"""));
    }
}
