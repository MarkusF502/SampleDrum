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

    // ---- Output attribution (review of 75dc01e): audio must START on the routed output only. ----

    private static (KitScenario Sc, HarnessResult Good) GoodRun()
    {
        var sc = KitScenario.Generate(1);
        var good = KitHarness.RunOffline(sc);
        Assert.True(good.Report.Passed, good.Report.SummaryTable());
        return (sc, good);
    }

    private static HarnessReport Reevaluate(KitScenario sc, HarnessResult good, SortedDictionary<int, float[]> physical)
    {
        var hits = good.Events.Where(e => !e.Event.IsManualTest).Select(KitHarness.ToHit).ToList();
        return ScenarioEvaluator.Evaluate(sc, hits, physical, good.InputDesk, good.OutputDesk);
    }

    private static SortedDictionary<int, float[]> CopyOutputs(HarnessResult r) =>
        new(r.PhysicalOutputs.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()));

    [Fact]
    public void ProbeKickReturnCopiedOntoUsb2Fails()
    {
        var (sc, good) = GoodRun();
        var physical = CopyOutputs(good);
        for (int i = 0; i < sc.Length; i++) physical[2][i] += physical[1][i];   // kick ALSO on the snare's output
        var rep = Reevaluate(sc, good, physical);
        Assert.False(rep.Passed);
        var kick = rep.Rows.Where(r => r.Piece == 0 && r.ExpectTrigger).ToList();
        Assert.All(kick, r => Assert.Equal(HarnessStatus.WrongOutput, r.Status));
        Assert.All(kick, r => Assert.Equal(2, r.DetectedOutputDesk));
        Assert.Equal(kick.Count, rep.Summaries[0].WrongOutput);
    }

    [Fact]
    public void ProbeConstantToneInsteadOfVoicesFails()
    {
        var (sc, good) = GoodRun();
        var physical = CopyOutputs(good);
        physical[1] = Enumerable.Repeat(0.01f, sc.Length).ToArray();        // -40 dBFS, no kick voices at all
        var rep = Reevaluate(sc, good, physical);
        Assert.False(rep.Passed);
        Assert.All(rep.Rows.Where(r => r.Piece == 0 && r.ExpectTrigger), r => Assert.Equal(HarnessStatus.WrongOutput, r.Status));
        Assert.Contains(rep.Rows, r => r.Piece == 0 && r.Status == HarnessStatus.Extra && r.Label.Contains("sem disparo"));
    }

    [Fact]
    public void RingingEarlierVoiceDoesNotCountAsTheNewOnset()
    {
        var (sc, good) = GoodRun();
        var physical = CopyOutputs(good);
        // Remove only the second voice of the kick double (+120 ms): the first voice is still ringing there.
        var second = sc.Expected.Single(e => e.Piece == 0 && e.Label.StartsWith("dupla fora do intervalo (2ª", StringComparison.Ordinal));
        var ev = good.Events.Single(e => e.Piece == DrumPiece.Kick && Math.Abs(e.Event.SampleIndex - second.SampleIndex) < 480).Event;
        var s = sc.Samples[0];
        for (int k = 0; k < s.Length && ev.PlaybackSampleIndex + k < sc.Length; k++)
            physical[1][ev.PlaybackSampleIndex + k] -= s[k] * ev.Gain;
        Assert.True(Math.Abs(physical[1][ev.PlaybackSampleIndex + 10]) > 1e-3f, "a voz anterior deveria continuar soando");
        var rep = Reevaluate(sc, good, physical);
        var row = rep.Rows.Single(r => r.Piece == 0 && r.ExpectedSample == second.SampleIndex);
        Assert.Equal(HarnessStatus.WrongOutput, row.Status);
        Assert.Equal(1, rep.Summaries[0].WrongOutput);
        Assert.False(rep.Passed);
    }

    [Fact]
    public void StrayAudioOnACorrectOutputIsExtra()
    {
        var (sc, good) = GoodRun();
        var physical = CopyOutputs(good);
        int at = (int)(0.25 * sc.SampleRate);                                // silence before the first hit
        for (int k = 0; k < 480; k++) physical[3][at + k] += 0.05f * MathF.Sin(k * 0.3f);
        var rep = Reevaluate(sc, good, physical);
        Assert.False(rep.Passed);
        var extra = Assert.Single(rep.Rows, r => r.Status == HarnessStatus.Extra);
        Assert.Equal(2, extra.Piece);                                         // USB 3 = Tom in the Simulação profile
        Assert.InRange(extra.DetectedSample!.Value, at - 48, at + 48);
    }

    [Fact]
    public void CorrectRunHasNoUnexplainedOutputAudio()
    {
        var (_, good) = GoodRun();
        Assert.DoesNotContain(good.Report.Rows, r => r.Label.Contains("sem disparo"));
        Assert.All(good.Report.Rows.Where(r => r.ExpectTrigger), r => Assert.Equal(r.OutputDesk, r.DetectedOutputDesk));
    }

    [Fact]
    public void SoftFloorTomHitsFireAndBelowThresholdIsKnownLimitation()
    {
        var (sc, good) = GoodRun();
        Assert.Equal(-16f, sc.ThresholdDb[3]);
        var soft = good.Report.Rows.Where(r => r.Piece == 3 && r.Label.StartsWith("surdo suave", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, soft.Count);
        foreach (var db in new[] { "-10", "-13", "-15" })
            Assert.Equal(HarnessStatus.Ok, Assert.Single(soft, r => r.Label.StartsWith($"surdo suave {db} ", StringComparison.Ordinal)).Status);
        var below = Assert.Single(soft, r => r.Label.StartsWith("surdo suave -18 ", StringComparison.Ordinal));
        Assert.Equal(HarnessStatus.KnownLimitation, below.Status);
        Assert.Null(below.DetectedSample);
        Assert.Equal(1, good.Report.Summaries[3].KnownLimitations);
        Assert.True(good.Report.Passed); // a known limitation does not fail the run
        // The kick bleed case is still there and still silent on the floor tom.
        Assert.Contains(good.Report.Rows, r => r.Piece == 3 && r.Label.StartsWith("vazamento", StringComparison.Ordinal) && r.Status == HarnessStatus.Ok);
    }

    [Fact]
    public void EventsAndRowsAreOrderedBySampleIndex()
    {
        var (_, good) = GoodRun();
        var idx = good.Events.Select(e => e.Event.SampleIndex).ToList();
        Assert.Equal(idx.OrderBy(x => x), idx);
        var simultaneous = good.Events.GroupBy(e => e.Event.SampleIndex).Where(g => g.Count() > 1).ToList();
        Assert.NotEmpty(simultaneous);
        Assert.All(simultaneous, g => Assert.Equal(g.Select(e => (int)e.Piece).OrderBy(x => x), g.Select(e => (int)e.Piece)));
        var rowKeys = good.Report.Rows.Select(r => r.ExpectedSample ?? r.DetectedSample ?? 0).ToList();
        Assert.Equal(rowKeys.OrderBy(x => x), rowKeys);
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
