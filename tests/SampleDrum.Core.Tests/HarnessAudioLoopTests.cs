using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.Core.Tests;

/// <summary><see cref="SimulatedAudioLoop"/>: the cross-platform continuous audio stand-in used by the panel E2E tests.</summary>
public class HarnessAudioLoopTests
{
    [Fact]
    public void ProcessesContinuously_CapturesReturns_RecordsKitPerBlock_AndStopJoins()
    {
        var sc = KitScenario.Generate(1);
        var engine = KitHarness.CreateEngine(sc);
        var kitA = engine.ActiveKit!;
        using var loop = new SimulatedAudioLoop(engine, sc.Mics, blockSize: 128, speed: 20, captureSeconds: 3);
        loop.Start();
        Assert.True(loop.WaitForPosition(48000, TimeSpan.FromSeconds(20)));    // 1 s of audio
        var kitB = new PreparedKit("B", 48000, DrumPieces.All.Select(p => new PreparedPiece(p, sc.Samples[(int)p].Select(v => -v).ToArray(), null, "B")));
        engine.ApplyKit(kitB);
        Assert.True(loop.WaitForPosition(4 * 48000, TimeSpan.FromSeconds(20)));
        loop.Stop();
        long callbacks = loop.Callbacks;
        Thread.Sleep(50);
        Assert.Equal(callbacks, loop.Callbacks);                                 // nothing after Stop returned
        Assert.False(loop.IsRunning);

        var (byPiece, kits, gens) = loop.GetCapture();
        Assert.All(byPiece, b => Assert.Equal(3 * 48000, b.Length));
        Assert.Equal(3 * 48000 / 128, kits.Length);
        int swap = Array.FindIndex(kits, k => ReferenceEquals(k, kitB));
        Assert.True(swap > 0);
        Assert.All(kits[..swap], k => Assert.Same(kitA, k));
        Assert.All(kits[swap..], k => Assert.Same(kitB, k));
        Assert.True(gens[swap] > gens[swap - 1]);
        Assert.Contains(byPiece[0], v => v != 0f);                               // the kick hit at 0.5 s played
        while (engine.TryDequeueEvent(out _)) { }                                // loop never consumed the event queue
    }
}
