using SampleDrum.App.Audio;
using SampleDrum.App.Harness;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Tests;

/// <summary>
/// Windows-only harness checks: the real-time simulation host (<see cref="FakeAudioHost"/>) and the preview ring.
/// Same logic as <c>SampleDrum.App.exe --selftest</c> (<see cref="RealtimeChecks"/>).
/// </summary>
public class HarnessRealtimeTests
{
    [Fact]
    public void RealtimeRunMatchesScenarioAndOfflineRun()
    {
        var (ok, detail) = RealtimeChecks.RealtimeMatchesScenario(128, speed: 8.0);
        Assert.True(ok, detail);
    }

    [Fact]
    public void ApplyKitWhileFakeHostRuns()
    {
        var (ok, detail) = RealtimeChecks.ApplyKitWhileRunning(speed: 4.0);
        Assert.True(ok, detail);
    }

    /// <summary>Pass-through processor: return = input (lets the host routing be checked without the engine).</summary>
    private sealed class PassThrough : IMultiChannelProcessor
    {
        public int PieceCount => 4;
        public long Calls;
        public void Process(float[][] inputs, float[][] outputs, int frames)
        {
            Calls++;
            for (int p = 0; p < 4; p++)
                for (int i = 0; i < frames; i++) outputs[p][i] += inputs[p][i];
        }
    }

    [Fact]
    public void HostFeedsOnlyRoutedInputsAndDiscardsUnroutedReturns()
    {
        // Tom pending: no input, no output. Others like the Simulation profile (in USB 3/4/6, out USB 1/2/4).
        var routing = new ResolvedRouting("teste", [2, 3, null, 5], [0, 1, null, 3]);
        var proc = new PassThrough();
        var sc = KitScenario.Generate(1);
        using var host = new FakeAudioHost(proc, routing, new FakeHostOptions
        {
            Source = SimulationSource.SyntheticKit, Seed = 1, BlockSize = 480, Loop = false, CaptureSeconds = 20, Speed = 50,
        });
        host.Start();
        Assert.True(host.WaitForFinish(TimeSpan.FromSeconds(30)));
        host.Stop();
        var cap = host.GetCapture()!.Value;
        Assert.True(cap.Frames >= sc.Length);
        Assert.Equal(new[] { 1, 2, 4 }, cap.Physical.Keys.ToArray());           // USB outputs actually written
        for (int p = 0; p < 4; p++)
        {
            if (p == 2) { Assert.All(cap.ByPiece[p], v => Assert.Equal(0f, v)); continue; } // unrouted: nothing
            Assert.Equal(sc.Mics[p], cap.ByPiece[p][..sc.Length]);               // routed: exact mic, sample-aligned
        }
        Assert.Contains(host.LoadNotes, n => n.Contains("Tom", StringComparison.Ordinal));
        Assert.Equal((long)Math.Ceiling(sc.Length / 480.0), host.CallbackCount);
    }

    [Fact]
    public void NonLoopingSourceRaisesFinishedAndStops()
    {
        bool finished = false;
        var routing = RoutingValidator.Validate(RoutingProfiles.Simulation).Resolve();
        using var host = new FakeAudioHost(new PassThrough(), routing, new FakeHostOptions { Loop = false, Speed = 50 });
        host.Finished += () => finished = true;
        host.Start();
        Assert.True(host.WaitForFinish(TimeSpan.FromSeconds(30)));
        Assert.True(finished);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void LoopingSourceWrapsAndStopJoins()
    {
        var routing = RoutingValidator.Validate(RoutingProfiles.Simulation).Resolve();
        var proc = new PassThrough();
        var host = new FakeAudioHost(proc, routing, new FakeHostOptions { Loop = true, BlockSize = 64, Speed = 20 });
        host.Start();
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (host.PositionSamples <= host.SourceLengthSamples && DateTime.UtcNow < limit) Thread.Sleep(20);
        host.Stop();
        long calls = proc.Calls;
        Thread.Sleep(100);
        Assert.True(host.PositionSamples > host.SourceLengthSamples, "a fonte deveria ter dado a volta");
        Assert.Equal(calls, proc.Calls); // no Process call after Stop returned
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void MissingMicFileGivesPtBrError()
    {
        var routing = RoutingValidator.Validate(RoutingProfiles.Simulation).Resolve();
        using var host = new FakeAudioHost(new PassThrough(), routing, new FakeHostOptions
        {
            Source = SimulationSource.MicWavs, MicPaths = [@"C:\nao\existe\bumbo.wav", null, null, null],
        });
        var ex = Assert.Throws<AudioHostException>(host.Start);
        Assert.Contains("Bumbo", ex.Message);
    }

    [Fact]
    public void PreviewRingDropsWhenFullWithoutAllocating()
    {
        using var player = new PreviewPlayer(48000); // not started: nothing consumes the ring
        var block = new float[512];
        player.Write(block); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) player.Write(block); // ~2.1 s of stereo > ring capacity (~1.4 s)
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(player.Overruns > 0);
        Assert.Equal(0, player.Underruns);
    }
}
