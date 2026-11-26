using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>Regression tests for the read-only review of the panel API (544cdc2): items (f)-(j).</summary>
public sealed class PanelRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-robust-" + Guid.NewGuid().ToString("N"));
    private readonly string _a, _b;

    public PanelRobustnessTests()
    {
        _a = Path.Combine(_dir, "a");
        _b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        WavFile.WriteMono(Path.Combine(_a, "a.wav"), Enumerable.Repeat(0.25f, 16).ToArray(), Fs);
        WavFile.WriteMono(Path.Combine(_b, "b.wav"), Enumerable.Repeat(0.75f, 16).ToArray(), Fs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Task<KitOperationSnapshot> Done(KitOperation op) => op.Completion.WaitAsync(TimeSpan.FromSeconds(15));

    // ---------- (f) invalid input / apply exception never kill the worker ----------

    [Fact]
    public async Task NullTrigger_FailsThatOpWithCode_AndLaterOpsStillComplete()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        var before = engine.Settings[DrumPiece.Kick];

        var bad = await Done(controller.SetPieceSettings(DrumPiece.Kick, new PieceSettings { Trigger = null! }));
        Assert.Equal(KitOperationStatus.Failed, bad.Status);
        Assert.Equal(PieceLoadErrorCode.InvalidArgument, Assert.Single(bad.Errors).Code);
        Assert.Equal(before, engine.Settings[DrumPiece.Kick]);

        var nanGain = await Done(controller.SetPieceGain(DrumPiece.Kick, float.NaN));          // no exception thrown
        Assert.Equal(PieceLoadErrorCode.InvalidArgument, Assert.Single(nanGain.Errors).Code);
        var badKit = await Done(controller.ApplyKit(KitTestUtil.DcKit(4),
            KitSettings.Default.With(DrumPiece.Tom, new PieceSettings { Trigger = null! })));
        Assert.Equal(KitOperationStatus.Failed, badKit.Status);

        var next = await Done(controller.SetKitEnabled(true));                                   // the probe that hung
        Assert.Equal(KitOperationStatus.Applied, next.Status);
        Assert.True(engine.KitEnabled);
        Assert.Equal(1, controller.Version);                                                     // failures do not count
    }

    [Fact]
    public async Task InvalidRequest_DoesNotSupersedeValidPendingWork()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        var good = controller.SetPieceSettings(DrumPiece.Snare, PieceSettings.DefaultFor(DrumPiece.Snare) with { OutputGainDb = -4f });
        var bad = controller.SetPieceSettings(DrumPiece.Snare, new PieceSettings { Trigger = null! });
        Assert.Equal(KitOperationStatus.Applied, (await Done(good)).Status);
        Assert.Equal(KitOperationStatus.Failed, (await Done(bad)).Status);
        Assert.Equal(-4f, engine.Settings[DrumPiece.Snare].OutputGainDb);
    }

    [Fact]
    public async Task ExceptionWhileApplying_FailsOnlyThatOp_WorkerKeepsRunning()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        controller.BeforeApply = s =>
        {
            if (s.Kind == KitOperationKind.PieceEnabled) throw new InvalidOperationException("falha simulada");
        };
        var boom = await Done(controller.SetPieceEnabled(DrumPiece.Tom, false));
        Assert.Equal(KitOperationStatus.Failed, boom.Status);
        Assert.Equal(PieceLoadErrorCode.Internal, Assert.Single(boom.Errors).Code);
        Assert.Contains("falha simulada", boom.Errors[0].Message);

        controller.BeforeApply = null;
        Assert.Equal(KitOperationStatus.Applied, (await Done(controller.SetKitEnabled(true))).Status);
        Assert.Equal(KitOperationStatus.Applied, (await Done(controller.SetPieceSample(DrumPiece.Kick, "a.wav"))).Status);
    }

    // ---------- (g) samples folder as a controller operation ----------

    [Fact]
    public async Task SetLibraryFolder_SameControllerVersionKeepsIncreasing_SubscribersKept()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        int notifications = 0;
        controller.StateChanged += (_, _) => Interlocked.Increment(ref notifications);

        Assert.Equal(KitOperationStatus.Applied, (await Done(controller.SetPieceSample(DrumPiece.Kick, "a.wav"))).Status);
        Assert.Equal(1, controller.Version);
        var folder = await Done(controller.SetLibraryFolder(_b));
        Assert.Equal(KitOperationStatus.Applied, folder.Status);
        Assert.Equal(2, controller.Version);
        Assert.Equal(Path.GetFullPath(_b), controller.Library.Folder);
        int before = Volatile.Read(ref notifications);

        var snare = await Done(controller.SetPieceSample(DrumPiece.Snare, "b.wav"));            // resolved in the NEW folder
        Assert.Equal(KitOperationStatus.Applied, snare.Status);
        Assert.Equal(3, controller.Version);
        Assert.Equal("b.wav", controller.Snapshot.Pieces[1].SampleId);
        Assert.Null(controller.Snapshot.Pieces[0].SampleId);                                     // a.wav is outside the new folder
        Assert.True(Volatile.Read(ref notifications) > before);                                  // same subscriber still notified

        var missing = await Done(controller.SetPieceSample(DrumPiece.Tom, "a.wav"));
        Assert.Equal(PieceLoadErrorCode.FileNotFound, Assert.Single(missing.Errors).Code);
    }

    [Fact]
    public async Task SetLibraryFolder_Invalid_FailsAndKeepsTheOldFolder()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        var bad = await Done(controller.SetLibraryFolder("pasta\0ruim"));
        Assert.Equal(KitOperationStatus.Failed, bad.Status);
        Assert.Equal(PieceLoadErrorCode.InvalidArgument, Assert.Single(bad.Errors).Code);
        var empty = await Done(controller.SetLibraryFolder("  "));
        Assert.Equal(KitOperationStatus.Failed, empty.Status);
        Assert.Equal(Path.GetFullPath(_a), controller.Library.Folder);
        Assert.Equal(0, controller.Version);
    }

    // ---------- (h) startup restore keeps ActivePresetId when the pieces match ----------

    private AppConfig ConfigWith(string? kick, string? snare)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.SamplesFolder = _a;
        cfg[DrumPiece.Kick].SamplePath = kick;
        cfg[DrumPiece.Snare].SamplePath = snare;
        cfg.ActivePresetId = "domingo";
        return cfg;
    }

    private static KitPreset Preset(string id, string? kick, string? snare) => new()
    {
        Id = id, Name = "Domingo",
        Pieces = new Dictionary<DrumPiece, PiecePreset?> { [DrumPiece.Kick] = new(kick), [DrumPiece.Snare] = new(snare) }
            .Where(kv => kv.Value!.SamplePath is not null).ToDictionary(kv => kv.Key, kv => kv.Value!),
    };

    [Fact]
    public void ToStartupPreset_KeepsIdOnlyWhenPiecesMatch_SamplesOnly()
    {
        var cfg = ConfigWith(Path.Combine(_a, "a.wav"), null);
        var match = cfg.ToStartupPreset(Preset("domingo", "a.wav", null));                    // relative vs absolute: same file
        Assert.Equal("domingo", match.Id);
        Assert.Equal(Path.Combine(_a, "a.wav"), match[DrumPiece.Kick]!.SamplePath);
        Assert.Null(match[DrumPiece.Kick]!.GainDb);                                              // calibration untouched
        Assert.Null(match[DrumPiece.Kick]!.Detection);

        Assert.Null(cfg.ToStartupPreset(Preset("domingo", "a.wav", "a.wav")).Id);              // preset has an extra piece
        Assert.Null(cfg.ToStartupPreset(Preset("outro", "a.wav", null)).Id);                   // different preset id
        Assert.Null(cfg.ToStartupPreset(null).Id);                                              // preset file gone
        cfg.ActivePresetId = null;
        Assert.Null(cfg.ToStartupPreset(Preset("domingo", "a.wav", null)).Id);
    }

    [Fact]
    public async Task StartupRestore_AsOneOperation_KeepsActivePresetId_AndSettings()
    {
        var cfg = ConfigWith(Path.Combine(_a, "a.wav"), null);
        cfg.SetPieceSettings(DrumPiece.Kick, PieceSettings.DefaultFor(DrumPiece.Kick) with { OutputGainDb = -7f });
        var engine = new KitEngine(Fs, cfg.ToKitSettings());
        await using var controller = new KitController(engine, new SampleLibrary(_a));
        var r = await Done(controller.ApplyPreset(cfg.ToStartupPreset(Preset("domingo", "a.wav", null))));
        Assert.Equal(KitOperationStatus.Applied, r.Status);
        Assert.Equal(1, controller.Version);
        Assert.Equal("domingo", controller.Snapshot.ActivePresetId);
        Assert.Equal(-7f, engine.Settings[DrumPiece.Kick].OutputGainDb);                        // calibration kept
        Assert.Equal("a.wav", controller.Snapshot.Pieces[0].SampleId);
    }

    // ---------- (i) NUL in a saved path ----------

    [Fact]
    public void Load_NulInSamplePathOrFolder_DroppedWithWarning_NoCrash()
    {
        string path = Path.Combine(_dir, AppConfig.FileName);
        File.WriteAllText(path, """
            { "SettingsVersion": 2, "SamplesFolder": "x\u0000y",
              "Pieces": [ { "Piece": "Kick", "SamplePath": "a\u0000b.wav" }, { "Piece": "Snare", "SamplePath": "ok.wav" } ] }
            """);
        var cfg = AppConfig.Load(path, out var msg);
        Assert.NotNull(msg);
        Assert.Contains("inválid", msg);
        Assert.Null(cfg[DrumPiece.Kick].SamplePath);
        Assert.Null(cfg.SamplesFolder);
        Assert.Equal(Path.Combine(cfg.EffectiveSamplesFolder, "ok.wav"), cfg[DrumPiece.Snare].SamplePath);
    }

    // ---------- (j) device names ----------

    [Theory]
    [InlineData("COM0.wav")]
    [InlineData("LPT0.wav")]
    [InlineData("COM¹.wav")]
    [InlineData("LPT³")]
    [InlineData("com5.wav")]
    [InlineData("NUL .wav")]
    [InlineData("CONIN$")]
    public void DeviceNames_Rejected(string name)
    {
        Assert.True(SampleLibrary.IsDeviceName(name));
        Assert.False(new SampleLibrary(_a).TryResolveInLibrary("pasta/" + name, out _));
    }

    [Theory]
    [InlineData("COM10.wav")]
    [InlineData("COMA.wav")]
    [InlineData("console.wav")]
    [InlineData("nulo.wav")]
    public void OrdinaryNames_Accepted(string name)
    {
        Assert.False(SampleLibrary.IsDeviceName(name));
        Assert.True(new SampleLibrary(_a).TryResolveInLibrary(name, out _));
    }
}
