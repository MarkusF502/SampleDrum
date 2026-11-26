using SampleDrum.Core;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.Core.Tests;

/// <summary>
/// Harness checks (same logic as <c>SampleDrum.App.exe --selftest</c>, see <see cref="HarnessChecks"/>) plus tests of
/// the harness itself: deterministic scenario, multichannel WAV round trip, evaluator catching failures, report files.
/// </summary>
public class HarnessTests
{
    private static void AssertCheck((bool ok, string detail) r) => Assert.True(r.ok, r.detail);

    [Fact] public void EachInputTriggersExpectedPieceAndOutput() => AssertCheck(HarnessChecks.RoutingPerPiece());
    [Fact] public void TwoOrMorePiecesTriggerTogether() => AssertCheck(HarnessChecks.Simultaneous());
    [Fact] public void DisablingOnePieceSilencesOnlyItsReturn() => AssertCheck(HarnessChecks.DisableOnePiece());
    [Fact] public void ManualTestReachesOnlyThatPiecesOutput() => AssertCheck(HarnessChecks.ManualTestPerPiece());
    [Fact] public void BlockCrossingHitsDetectedAtSamePositionForAllBlockSizes() => AssertCheck(HarnessChecks.BlockCrossing());
    [Fact] public void BleedDoesNotTriggerWrongPiece() => AssertCheck(HarnessChecks.BleedDoesNotTrigger());
    [Fact] public void ApplyKitMidRunKeepsOldVoicesAndUsesNewKit() => AssertCheck(HarnessChecks.ApplyKitMidRun());
    [Fact] public void V1SettingsMigrationKeepsKickProfile() => AssertCheck(HarnessChecks.MigrationKeepsKick(Path.GetTempPath()));

    [Fact]
    public void ScenarioIsDeterministicPerSeed()
    {
        var a = KitScenario.Generate(3);
        var b = KitScenario.Generate(3);
        var c = KitScenario.Generate(4);
        for (int p = 0; p < 4; p++)
        {
            Assert.Equal(a.Mics[p], b.Mics[p]);
            Assert.Equal(a.Samples[p], b.Samples[p]);
        }
        Assert.NotEqual(a.Mics[0], c.Mics[0]);
        Assert.Equal(a.Expected, b.Expected);
    }

    [Fact]
    public void ScenarioCoversRequiredCases()
    {
        var sc = KitScenario.Generate(1);
        Assert.Equal(48000, sc.SampleRate);
        Assert.All(sc.Mics, m => Assert.Equal(sc.Length, m.Length));
        for (int p = 0; p < 4; p++)
        {
            var exp = sc.Expected.Where(e => e.Piece == p).ToList();
            Assert.Contains(exp, e => e.Label == "isolada forte");
            Assert.Contains(exp, e => e.Label == "isolada suave");
            Assert.Contains(exp, e => e.Kind == ExpectationKind.DoubleInsideLockout && !e.ExpectTrigger);
            Assert.Contains(exp, e => e.Label.StartsWith("dupla fora", StringComparison.Ordinal) && e.ExpectTrigger);
            var cross = Assert.Single(exp, e => e.Label.StartsWith("atravessa", StringComparison.Ordinal));
            Assert.NotEqual(0, cross.SampleIndex % KitScenario.CommonBoundary); // starts just before a boundary
            Assert.True(KitScenario.CommonBoundary - cross.SampleIndex % KitScenario.CommonBoundary < 16);
        }
        foreach (int n in new[] { 2, 3, 4 })
            Assert.Contains(sc.Expected.Where(e => e.Label == $"simultânea ({n} peças)").GroupBy(e => e.SampleIndex), g => g.Count() == n);
        Assert.Contains(sc.Expected, e => e.Kind == ExpectationKind.Bleed && e.Piece == 3); // kick -> floor tom mic
        Assert.Contains(sc.Expected, e => e.Kind == ExpectationKind.Bleed && e.Piece == 2); // snare -> tom mic
        Assert.Equal(4, sc.Samples.Select(s => s.Length).Count());
    }

    [Fact]
    public void MultiChannelWavRoundTripsThroughCoreReader()
    {
        var sc = KitScenario.Generate(2);
        string path = Path.Combine(Path.GetTempPath(), $"sd-mc-{Guid.NewGuid():N}.wav");
        try
        {
            MultiChannelWav.Write(path, sc.Mics, sc.SampleRate);
            for (int ch = 0; ch < 4; ch++)
            {
                var d = WavFile.Read(path, ch);
                Assert.Equal(4, d.Channels);
                Assert.Equal(sc.SampleRate, d.SampleRate);
                Assert.Equal(sc.Mics[ch], d.Mono);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EvaluatorFlagsMissingExtraAndWrongOutput()
    {
        var sc = KitScenario.Generate(1);
        var good = KitHarness.RunOffline(sc);
        // Simulate a broken engine: drop the snare's detections, invent a tom hit, and send the kick's audio to USB 2.
        var hits = good.Events.Where(e => !e.Event.IsManualTest && e.Piece != DrumPiece.Snare).Select(KitHarness.ToHit).ToList();
        hits.Add(new DetectedHit(2, sc.SampleRate / 10, sc.SampleRate / 10, 0.5f, 0.5f, 1f, false));
        var physical = new SortedDictionary<int, float[]>(good.PhysicalOutputs.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()));
        physical[2] = physical[1].Zip(physical[2], (a, b) => a + b).ToArray();
        physical[1] = new float[sc.Length];
        var rep = ScenarioEvaluator.Evaluate(sc, hits, physical, good.InputDesk, good.OutputDesk);
        Assert.False(rep.Passed);
        Assert.True(rep.Summaries[1].Missing > 0);
        Assert.True(rep.Summaries[2].Extra > 0);
        Assert.True(rep.Summaries[0].WrongOutput > 0);
        Assert.Contains(rep.Rows, r => r.Status == HarnessStatus.WrongOutput && r.DetectedOutputDesk == 2);
    }

    [Fact]
    public void WritesAllReportFiles()
    {
        var sc = KitScenario.Generate(1);
        var o = new HarnessOptions { BlockSize = 128 };
        var r = KitHarness.RunOffline(sc, o);
        string dir = Path.Combine(Path.GetTempPath(), $"sd-harness-{Guid.NewGuid():N}");
        try
        {
            var files = KitHarness.WriteFiles(r, dir, o).Select(Path.GetFileName).ToList();
            foreach (var name in new[] { "retorno-bumbo.wav", "retorno-caixa.wav", "retorno-tom.wav", "retorno-surdo.wav",
                                         "retornos-4ch.wav", "previa-estereo.wav", "relatorio.csv", "relatorio.md", "mics-4ch.wav" })
                Assert.Contains(name, files);
            Assert.Equal(4, WavFile.Read(Path.Combine(dir, "retornos-4ch.wav"), 0).Channels);
            Assert.Equal(2, WavFile.Read(Path.Combine(dir, "previa-estereo.wav"), 0).Channels);
            string md = File.ReadAllText(Path.Combine(dir, "relatorio.md"));
            Assert.Contains("Resumo por peça", md);
            Assert.Contains("Bumbo", File.ReadAllText(Path.Combine(dir, "relatorio.csv")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void PreviewMixIsAllocationFreeAndRespectsPieceMask()
    {
        var returns = Enumerable.Range(0, 4).Select(p => Enumerable.Repeat(0.5f, 64).ToArray()).ToArray();
        var stereo = new float[128];
        var only = new PreviewMixSettings { VolumeDb = 0f, IncludePiece = [false, false, true, false] };
        PreviewMix.MixInterleaved(returns, null, 64, stereo, only); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        PreviewMix.MixInterleaved(returns, null, 64, stereo, only);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(stereo[1] > stereo[0]); // tom is panned right
        var none = only with { IncludePiece = [false, false, false, false] };
        PreviewMix.MixInterleaved(returns, null, 64, stereo, none);
        Assert.All(stereo, v => Assert.Equal(0f, v));
    }
}
