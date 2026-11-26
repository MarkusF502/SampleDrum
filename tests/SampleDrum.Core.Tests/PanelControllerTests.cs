using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

public sealed class PanelControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-controller-" + Guid.NewGuid().ToString("N"));
    public PanelControllerTests()
    {
        Directory.CreateDirectory(_dir);
        WavFile.WriteMono(Path.Combine(_dir, "a.wav"), Enumerable.Repeat(0.25f, 16).ToArray(), Fs);
        WavFile.WriteMono(Path.Combine(_dir, "b.wav"), Enumerable.Repeat(0.75f, 16).ToArray(), Fs);
        WavFile.WriteMono(Path.Combine(_dir, "long.wav"), Enumerable.Repeat(0.25f, Fs).ToArray(), Fs);
    }
    public void Dispose() => Directory.Delete(_dir, true);
    private static Task<KitOperationSnapshot> Done(KitOperation op) => op.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    private static KitPreset Preset(string id, string file) => new()
    {
        Id = id, Name = "Kit",
        Pieces = DrumPieces.All.ToDictionary(p => p, _ => new PiecePreset(file)),
    };

    [Fact]
    public async Task FailedPrepareKeepsKitAndSnapshot_VersionIncrementsOncePerAppliedOperation()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<KitStateSnapshot>();
        controller.StateChanged += (_, _) => notifications.Enqueue(controller.Snapshot);
        controller.StateChanged += (_, _) => throw new Exception("subscriber failure");
        Assert.Equal(KitOperationStatus.Applied, (await Done(controller.ApplyPreset(Preset("first", "a.wav")))).Status);
        var oldKit = engine.ActiveKit;
        var oldState = controller.Snapshot;
        var bad = await Done(controller.ApplyPreset(Preset("bad", "missing.wav")));
        Assert.Equal(KitOperationStatus.Failed, bad.Status);
        Assert.All(bad.Errors, e => Assert.Equal(PieceLoadErrorCode.FileNotFound, e.Code));
        Assert.Same(oldKit, engine.ActiveKit);
        Assert.Same(oldState, controller.Snapshot);
        var outside = await Done(controller.SetPieceSample(DrumPiece.Kick, "../escape.wav"));
        Assert.Equal(PieceLoadErrorCode.OutsideLibrary, Assert.Single(outside.Errors).Code);
        Assert.Equal(1, controller.Version);
        Assert.Equal(2, (await Done(controller.SetPieceGain(DrumPiece.Kick, -3))).ResultVersion);
        Assert.Equal(3, (await Done(controller.SetPieceEnabled(DrumPiece.Snare, false))).ResultVersion);
        Assert.Equal(4, (await Done(controller.SetKitEnabled(true))).ResultVersion);
        Assert.Equal(5, (await Done(controller.SetPieceSample(DrumPiece.Tom, "b.wav"))).ResultVersion);
        var state = controller.Snapshot;
        Assert.True(state.KitEnabled);
        Assert.Null(state.ActivePresetId);
        Assert.Equal(-3, state.Pieces[0].GainDb);
        Assert.False(state.Pieces[1].Enabled);
        Assert.Equal("b.wav", state.Pieces[2].SampleId);
        Assert.Equal("a.wav", state.Pieces[3].SampleId);
        Assert.Equal(KitOperationStatus.Failed, controller.GetOperation(bad.Id)!.Status);
        Assert.NotEmpty(notifications);
    }

    [Fact]
    public async Task NewWholeKitSupersedesPreparingAndQueuedRequests_FromTwoThreads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new KitEngine(Fs, KitSettings.Default);
        var library = new SampleLibrary(_dir);
        async Task<KitPrepareResult> Prepare(KitPreset p, int rate, CancellationToken ct)
        {
            if (p.Id == "slow") { entered.SetResult(); await release.Task.WaitAsync(ct); }
            return await library.PrepareAsync(p, rate, ct);
        }
        await using var controller = new KitController(engine, library, Prepare);
        var slow = controller.ApplyPreset(Preset("slow", "a.wav"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var one = await Task.Run(() => controller.SetPieceSample(DrumPiece.Kick, "a.wav"));
        var two = await Task.Run(() => controller.ApplyPreset(Preset("latest", "b.wav")));
        Assert.Null(engine.ActiveKit);
        release.SetResult();
        Assert.Equal(KitOperationStatus.Superseded, (await Done(slow)).Status);
        Assert.Equal(KitOperationStatus.Superseded, (await Done(one)).Status);
        Assert.Equal(KitOperationStatus.Applied, (await Done(two)).Status);
        Assert.Equal(1, controller.Version);
        Assert.Equal("latest", controller.Snapshot.ActivePresetId);
        engine.Reset();
        Assert.Equal(1, engine.AppliedKitGeneration); // superseded operations never submitted
    }

    [Fact]
    public async Task NewPieceSupersedesOlderWholePreset_AndSamePiecePendingWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new KitEngine(Fs, KitSettings.Default);
        var library = new SampleLibrary(_dir);
        async Task<KitPrepareResult> Prepare(KitPreset p, int rate, CancellationToken ct)
        { entered.SetResult(); await release.Task.WaitAsync(ct); return await library.PrepareAsync(p, rate, ct); }
        await using var controller = new KitController(engine, library, Prepare);
        var preset = controller.ApplyPreset(Preset("old", "a.wav"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var first = controller.SetPieceSample(DrumPiece.Snare, "a.wav");
        var second = controller.SetPieceSample(DrumPiece.Snare, "b.wav");
        release.SetResult();
        Assert.Equal(KitOperationStatus.Superseded, (await Done(preset)).Status);
        Assert.Equal(KitOperationStatus.Superseded, (await Done(first)).Status);
        Assert.Equal(KitOperationStatus.Applied, (await Done(second)).Status);
        Assert.Equal(1, controller.Version);
        Assert.Null(controller.Snapshot.Pieces[0].SamplePath);
        Assert.Equal("b.wav", controller.Snapshot.Pieces[1].SampleId);
    }

    [Fact]
    public async Task NonOverlappingConcurrentChangesAreSerializedWithoutLosingEither()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        using var start = new Barrier(2);
        var a = Task.Factory.StartNew(() => { start.SignalAndWait(); return controller.SetPieceSample(DrumPiece.Kick, "a.wav"); }, TaskCreationOptions.LongRunning);
        var b = Task.Factory.StartNew(() => { start.SignalAndWait(); return controller.SetPieceSample(DrumPiece.Snare, "b.wav"); }, TaskCreationOptions.LongRunning);
        var operations = await Task.WhenAll(a, b);
        foreach (var op in operations) Assert.Equal(KitOperationStatus.Applied, (await Done(op)).Status);
        Assert.Equal(2, controller.Version);
        Assert.Equal("a.wav", controller.Snapshot.Pieces[0].SampleId);
        Assert.Equal("b.wav", controller.Snapshot.Pieces[1].SampleId);
    }

    [Fact]
    public async Task RunningSyntheticAudio_EveryBlockUsesOnePresetGeneration_NotificationsOffAudioThread()
    {
        var settings = KitTestUtil.AllPieces(Impulsive with { MinIntervalMs = 0 });
        var engine = new KitEngine(Fs, settings) { KitEnabled = true };
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        await Done(controller.ApplyPreset(Preset("a", "a.wav")));
        using var stop = new CancellationTokenSource();
        int audioThread = 0, blocks = 0;
        var eventThreads = new System.Collections.Concurrent.ConcurrentQueue<int>();
        controller.StateChanged += (_, _) => eventThreads.Enqueue(Environment.CurrentManagedThreadId);
        var audio = Task.Factory.StartNew(() =>
        {
            audioThread = Environment.CurrentManagedThreadId;
            var input = DrumPieces.All.Select(_ => new float[128]).ToArray();
            var output = DrumPieces.All.Select(_ => new float[128]).ToArray();
            while (!stop.IsCancellationRequested)
            {
                int block = blocks;
                foreach (var i in input) { Array.Clear(i); if (block % 40 == 0) i[30] = 0.5f; }
                foreach (var o in output) Array.Clear(o);
                engine.Process(input, output, 128);
                for (int p = 1; p < 4; p++) Assert.Equal(output[0], output[p]);
                while (engine.TryDequeueEvent(out _)) { }
                Interlocked.Increment(ref blocks);
            }
        }, TaskCreationOptions.LongRunning);
        try
        {
            for (int i = 0; i < 60; i++)
                Assert.Equal(KitOperationStatus.Applied, (await Done(controller.ApplyPreset(Preset(i.ToString(), i % 2 == 0 ? "b.wav" : "a.wav")))).Status);
        }
        finally { stop.Cancel(); await audio.WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.True(blocks > 0);
        Assert.Equal(61, controller.Version);
        Assert.NotEmpty(eventThreads);
        Assert.DoesNotContain(audioThread, eventThreads);
    }

    [Fact]
    public async Task SinglePieceSelection_KeepsOldVoicesUntilTheyFinish()
    {
        var engine = new KitEngine(Fs, KitTestUtil.AllPieces(Impulsive)) { KitEnabled = true };
        await using var controller = new KitController(engine, new SampleLibrary(_dir));
        await Done(controller.SetPieceSample(DrumPiece.Kick, "long.wav"));
        var input = KitTestUtil.Mics(128.0 / Fs);
        var output = KitTestUtil.Mics(128.0 / Fs);
        input[0][10] = 0.5f;
        engine.Process(input, output, 128);
        Assert.Equal(0.25f, output[0][127]);
        await Done(controller.SetPieceSample(DrumPiece.Kick, "b.wav"));
        Array.Clear(input[0]);
        for (int n = 0; n < 41; n++)
        {
            foreach (var o in output) Array.Clear(o);
            engine.Process(input, output, 128);
            Assert.Equal(0.25f, output[0][0]);
        }
        input[0][20] = 0.5f;
        foreach (var o in output) Array.Clear(o);
        engine.Process(input, output, 128);
        Assert.Equal(0.25f, output[0][19]);
        Assert.Equal(1f, output[0][20]);
        Assert.Equal(0.25f, output[0][40]); // new short voice finished, old voice remains
    }

    [Fact]
    public async Task DisposalSupersedesPendingWorkAndRejectsNewCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<KitPrepareResult> Prepare(KitPreset p, int rate, CancellationToken ct)
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
        var controller = new KitController(new KitEngine(Fs, KitSettings.Default), new SampleLibrary(_dir), Prepare);
        var op = controller.ApplyPreset(Preset("slow", "a.wav"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var pending = controller.SetKitEnabled(true);
        await controller.DisposeAsync();
        Assert.Equal(KitOperationStatus.Superseded, (await Done(op)).Status);
        Assert.Equal(KitOperationStatus.Superseded, (await Done(pending)).Status);
        Assert.Throws<ObjectDisposedException>(() => controller.SetKitEnabled(true));
    }
}
