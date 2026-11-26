using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

public class PanelEngineTests
{
    [Fact]
    public void PendingKitSurvivesReset_AndGenerationAcknowledgesConsumedSwap()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        var first = KitTestUtil.DcKit(16, 0.25f);
        engine.ApplyKit(first);
        Assert.Same(first, engine.ActiveKit);
        Assert.Null(engine.PlayingKit);
        Assert.Equal(0, engine.AppliedKitGeneration);
        Assert.Throws<ArgumentException>(() => engine.ApplyKit(PreparedKit.Empty(44100)));
        Assert.Same(first, engine.ActiveKit);
        engine.Reset();
        Assert.Same(first, engine.PlayingKit);
        Assert.Equal(1, engine.AppliedKitGeneration);
        engine.ApplyKit(KitTestUtil.DcKit(16, 0.5f));
        engine.ApplyKit(KitTestUtil.DcKit(16, 0.75f));
        engine.Process(KitTestUtil.Mics(128.0 / Fs), KitTestUtil.Mics(128.0 / Fs), 128);
        Assert.Equal(3, engine.AppliedKitGeneration);
        Assert.Same(engine.ActiveKit, engine.PlayingKit);
    }

    [Fact]
    public void ReplacingUnprocessedKit_KeepsSubmittedGainAndDetection()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        var kit = KitTestUtil.DcKit(128).WithPiece(new PreparedPiece(DrumPiece.Snare, [1f], null, null)
        { GainDb = -6.0206f, Detection = new TriggerOverrides { ThresholdDb = -12f } });
        engine.ApplyKit(kit);
        engine.ApplyKit(kit.SamplesOnly());
        Assert.Equal(-12f, engine.Settings[DrumPiece.Snare].Trigger.ThresholdDb);
        engine.RequestTest(DrumPiece.Snare);
        var output = KitTestUtil.Mics(128.0 / Fs);
        engine.Process(KitTestUtil.Mics(128.0 / Fs), output, 128);
        Assert.Equal(0.5f, output[1][0], 4);
    }
}
