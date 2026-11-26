using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.KitTestUtil;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

/// <summary>Four-piece engine: independence, enables, tests, block invariance, bleed, kit swap, allocation.</summary>
public class KitEngineTests
{
    // A 200 Hz cosine burst starting at full amplitude: crosses every piece's threshold on its first sample
    // (passes all per-piece high-passes), so the onset is exactly the burst start.
    private static void Hit(float[] mic, long at, float levelDb = -6f) => AddCosBurst(mic, at, levelDb, 200, 20);

    private static long Window(DrumPiece p) =>
        Math.Max(Ms(KitDefaults.TriggerFor(p).DetectTimeMs), Ms(KitDefaults.TriggerFor(p).ScanMs));

    [Theory]
    [InlineData(DrumPiece.Kick)]
    [InlineData(DrumPiece.Snare)]
    [InlineData(DrumPiece.Tom)]
    [InlineData(DrumPiece.FloorTom)]
    public void EachInput_TriggersOnlyItsOwnPiece(DrumPiece piece)
    {
        var mics = Mics(1);
        Hit(mics[(int)piece], 4800);
        var (outs, events) = Run(Engine(), mics, 128);

        var e = Assert.Single(events);
        Assert.Equal(piece, e.Piece);
        Assert.Equal(4800, e.Event.SampleIndex);
        foreach (var p in DrumPieces.All)
        {
            if (p == piece) Assert.Equal((int)(4800 + Window(p)), FirstNonZero(outs[(int)p]));
            else Assert.All(outs[(int)p], v => Assert.Equal(0f, v));
        }
    }

    [Fact]
    public void SeveralPiecesInTheSameBlock_SampleExact_OutputsIndependent()
    {
        long[] onsets = { 1000, 1003, 1010, 1010 };   // all inside block 7 (896..1023) for 128-sample blocks
        var mics = Mics(1);
        foreach (var p in DrumPieces.All) Hit(mics[(int)p], onsets[(int)p]);
        var (outs, events) = Run(Engine(), mics, 128);

        Assert.Equal(4, events.Count);
        foreach (var p in DrumPieces.All)
        {
            var e = Assert.Single(events, x => x.Piece == p);
            Assert.Equal(onsets[(int)p], e.Event.SampleIndex);
            Assert.Equal(onsets[(int)p] + Window(p), e.Event.PlaybackSampleIndex);

            // Identical to running that piece alone: nothing leaks between pieces.
            var solo = Mics(1);
            Hit(solo[(int)p], onsets[(int)p]);
            var (soloOuts, _) = Run(Engine(), solo, 128);
            Assert.Equal(soloOuts[(int)p], outs[(int)p]);
        }
    }

    [Fact]
    public void DisablingOnePiece_SilencesOnlyItsReturn()
    {
        var mics = Mics(1);
        foreach (var p in DrumPieces.All) Hit(mics[(int)p], 4800 + 500 * (int)p);
        var all = Run(Engine(), mics, 128);

        var engine = Engine();
        engine.SetPieceEnabled(DrumPiece.Snare, false);
        Assert.False(engine.IsPieceEnabled(DrumPiece.Snare));
        var (outs, events) = Run(engine, mics, 128);

        Assert.All(outs[(int)DrumPiece.Snare], v => Assert.Equal(0f, v));
        Assert.DoesNotContain(events, e => e.Piece == DrumPiece.Snare);
        foreach (var p in new[] { DrumPiece.Kick, DrumPiece.Tom, DrumPiece.FloorTom })
            Assert.Equal(all.Outputs[(int)p], outs[(int)p]);
    }

    [Fact]
    public void KitMasterDisabled_SilencesAll_ButManualTestsPlay()
    {
        var mics = Mics(1);
        foreach (var p in DrumPieces.All) Hit(mics[(int)p], 4800);
        var engine = Engine(enabled: false);
        Assert.False(new KitEngine(Fs, KitSettings.Default).KitEnabled);   // starts disabled

        var (outs, events) = Run(engine, mics, 128);
        Assert.Empty(events);
        Assert.All(outs, o => Assert.All(o, v => Assert.Equal(0f, v)));

        foreach (var p in DrumPieces.All) engine.RequestTest(p, 0.5f);
        var (testOuts, testEvents) = Run(engine, Mics(0.01), 128);
        Assert.Equal(4, testEvents.Count);
        Assert.All(testEvents, e => Assert.True(e.Event.IsManualTest));
        foreach (var p in DrumPieces.All) Assert.Equal(0.5f, testOuts[(int)p][0]);
    }

    [Theory]
    [InlineData(DrumPiece.Kick)]
    [InlineData(DrumPiece.Snare)]
    [InlineData(DrumPiece.Tom)]
    [InlineData(DrumPiece.FloorTom)]
    public void ManualTest_PlaysOnlyThatPiece(DrumPiece piece)
    {
        var engine = Engine(enabled: false, kit: DcKit(64, 0.1f, 0.2f, 0.3f, 0.4f));
        engine.RequestTest(piece);
        var (outs, events) = Run(engine, Mics(0.01), 128);
        var e = Assert.Single(events);
        Assert.Equal(piece, e.Piece);
        Assert.True(e.Event.IsManualTest);
        foreach (var p in DrumPieces.All)
        {
            if (p == piece) Assert.Equal(0.1f * ((int)p + 1), outs[(int)p][0], 6);
            else Assert.All(outs[(int)p], v => Assert.Equal(0f, v));
        }
    }

    [Fact]
    public void PerPieceOutputGain_AppliesOnlyToThatPiece()
    {
        var engine = Engine(enabled: false);
        engine.UpdatePieceSettings(DrumPiece.Tom, PieceSettings.DefaultFor(DrumPiece.Tom) with { OutputGainDb = -6.0206f });
        foreach (var p in DrumPieces.All) engine.RequestTest(p);
        var (outs, _) = Run(engine, Mics(0.01), 128);
        Assert.Equal(0.5f, outs[(int)DrumPiece.Tom][0], 4);
        Assert.Equal(1f, outs[(int)DrumPiece.Kick][0], 6);
        Assert.Equal(-6.0206f, engine.Settings[DrumPiece.Tom].OutputGainDb, 3);
    }

    [Fact]
    public void PerPieceSettings_AreIndependent()
    {
        var mics = Mics(1);
        foreach (var p in DrumPieces.All) Hit(mics[(int)p], 4800, -15f);     // above all default thresholds
        var engine = Engine();
        engine.UpdatePieceSettings(DrumPiece.Snare, PieceSettings.DefaultFor(DrumPiece.Snare) with
        {
            Trigger = KitDefaults.Snare.WithThreshold(-10f),                 // now above the hit level
        });
        var (_, events) = Run(engine, mics, 128);
        Assert.Equal(3, events.Count);
        Assert.DoesNotContain(events, e => e.Piece == DrumPiece.Snare);
    }

    public static IEnumerable<object[]> BlockSizes() =>
        new[] { 1, 7, 32, 61, 64, 127, 128, 256, 480, 512, 1000, 1024 }.Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(BlockSizes))]
    public void HitsAcrossBlockBoundaries_PerPiece_IdenticalToWholeBuffer(int block)
    {
        // Each piece gets a real kick-like hit starting 3, 2, 1 and 0 samples before a boundary common to all
        // power-of-two block sizes and 480, plus a second hit elsewhere.
        long b = SyntheticSignals.CommonBlockBoundary;
        var mics = Mics(6);
        foreach (var p in DrumPieces.All)
        {
            AddKick(mics[(int)p], b - 3 + (int)p, -6f, 60f, seed: 20 + (int)p);
            AddKick(mics[(int)p], Ms(500 + 300 * (int)p), -10f, 40f, seed: 40 + (int)p);
        }

        var whole = Run(Engine(), mics, mics[0].Length);
        var blocked = Run(Engine(), mics, block);

        // The queue interleaves pieces by block, so compare per piece (order within a piece must match).
        foreach (var piece in DrumPieces.All)
            Assert.Equal(whole.Events.Where(e => e.Piece == piece), blocked.Events.Where(e => e.Piece == piece));
        Assert.Equal(whole.Events.Count, blocked.Events.Count);
        for (int p = 0; p < 4; p++) Assert.Equal(whole.Outputs[p], blocked.Outputs[p]);
        foreach (var p in DrumPieces.All)
        {
            var ev = blocked.Events.Where(e => e.Piece == p).Select(e => e.Event.SampleIndex).ToList();
            Assert.Equal(2, ev.Count);
            Assert.InRange(ev[1], b - 3 + (int)p, b - 3 + (int)p + ToleranceSamples);
        }
    }

    [Fact]
    public void BleedBelowEachPieceThreshold_DoesNotTriggerTheWrongPiece()
    {
        // Each piece is hit in turn at -6 dBFS; the same hit bleeds into the three other mics 24 dB lower
        // (-30 dBFS, below every piece threshold). Only the struck piece may trigger.
        var mics = Mics(3);
        foreach (var p in DrumPieces.All)
        {
            long at = Ms(300 + 600 * (int)p);
            var hit = SyntheticSignals.Kick(Fs, Db.ToLinear(-6f), 80f, seed: 60 + (int)p, startHz: 150f + 60 * (int)p, endHz: 60f + 40 * (int)p);
            var bleed = hit.Select(v => v * Db.ToLinear(-24f)).ToArray();
            foreach (var q in DrumPieces.All) SyntheticSignals.Add(mics[(int)q], q == p ? hit : bleed, at);
        }
        var (_, events) = Run(Engine(), mics, 128);
        Assert.Equal(4, events.Count);
        foreach (var p in DrumPieces.All)
        {
            var e = Assert.Single(events, x => x.Piece == p);
            Assert.InRange(e.Event.SampleIndex, Ms(300 + 600 * (int)p), Ms(300 + 600 * (int)p) + ToleranceSamples);
        }
    }

    [Fact]
    public void SyntheticScenario_UsesExactlyTheKitDefaults()
    {
        var sc = SampleDrum.Core.Simulation.KitScenario.Generate(1, Fs);
        foreach (var p in DrumPieces.All)
            Assert.Equal(KitDefaults.TriggerFor(p).ThresholdDb, sc.ThresholdDb[(int)p]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void DefaultKit_PassesSyntheticScenario_KickBleedDoesNotFireFloorTom(int seed)
    {
        // Guards the floor-tom default (-16 dBFS): the scenario's hard kick leaks into the floor-tom mic at about
        // -21 dBFS, which fired the previous -22 dBFS default. Tolerance 2 ms (soft hits cross a little late).
        var sc = SampleDrum.Core.Simulation.KitScenario.Generate(seed, Fs);
        var (_, events) = Run(Engine(KitSettings.Default), sc.Mics, 256);
        long tol = 2 * ToleranceSamples;
        foreach (var x in sc.Expected)
        {
            var near = events.Where(e => (int)e.Piece == x.Piece && Math.Abs(e.Event.SampleIndex - x.SampleIndex) <= tol).ToList();
            if (x.ExpectTrigger) Assert.True(near.Count == 1, $"{(DrumPiece)x.Piece} {x.Label} @ {x.SampleIndex}: {near.Count} eventos");
            else Assert.True(near.Count == 0, $"{(DrumPiece)x.Piece} {x.Label} @ {x.SampleIndex}: disparou");
        }
        Assert.Equal(sc.Expected.Count(x => x.ExpectTrigger), events.Count);
    }

    [Fact]
    public void ApplyKit_OldVoicesFinishWithOldSample_NewHitsUseNewKit()
    {
        var settings = AllPieces(Impulsive);
        var engine = Engine(settings, DcKit(Fs, 1f, 1f, 1f, 1f));
        var inB = Mics(128.0 / Fs);
        var outB = Mics(128.0 / Fs);

        inB[0][10] = 0.5f;                                     // kick hit with kit A (DC 1.0, 1 s long)
        engine.Process(inB, outB, 128);
        Assert.Equal(1f, outB[0][127]);

        engine.ApplyKit(DcKit(Fs, 0.25f, 0.25f, 0.25f, 0.25f));  // kit B, effective at the next block
        Assert.Equal(0.25f, engine.ActiveKit![DrumPiece.Kick].Samples![0]);
        Assert.Equal(1f, engine.PlayingKit![DrumPiece.Kick].Samples![0]);

        // Next block, no hit: the old voice keeps playing kit A's sample.
        Array.Clear(inB[0]);
        foreach (var o in outB) Array.Clear(o);
        engine.Process(inB, outB, 128);
        Assert.All(outB[0], v => Assert.Equal(1f, v));
        Assert.Same(engine.ActiveKit, engine.PlayingKit);

        // Let the detector re-arm, then a new hit: kit B (0.25) on top of the old voice (1.0).
        for (int k = 0; k < 40; k++) { foreach (var o in outB) Array.Clear(o); engine.Process(inB, outB, 128); }
        inB[0][20] = 0.5f;
        foreach (var o in outB) Array.Clear(o);
        engine.Process(inB, outB, 128);
        Assert.Equal(1f, outB[0][19]);
        Assert.Equal(1.25f, outB[0][20]);
    }

    [Fact]
    public async Task ApplyKit_ConcurrentWithProcessing_NoBlockEverMixesTwoKits()
    {
        // Audio loop on this thread, ApplyKit hammered from another one. Every 40 blocks all four pieces hit at the
        // same sample; the four new voices must always come from the same kit (all 1.0 or all 0.25).
        var settings = AllPieces(Impulsive with { MinIntervalMs = 0f });
        var kitA = DcKit(16, 1f, 1f, 1f, 1f);
        var kitB = DcKit(16, 0.25f, 0.25f, 0.25f, 0.25f);
        var engine = Engine(settings, kitA);
        var inB = Mics(128.0 / Fs);
        var outB = Mics(128.0 / Fs);

        using var cts = new CancellationTokenSource();
        var swapper = Task.Run(() =>
        {
            bool a = false;
            while (!cts.IsCancellationRequested) { engine.ApplyKit(a ? kitA : kitB); a = !a; }
        });

        var seen = new HashSet<float>();
        for (int hit = 0; hit < 300; hit++)
        {
            for (int k = 0; k < 40; k++)
            {
                bool hitBlock = k == 0;
                foreach (var i in inB) { Array.Clear(i); if (hitBlock) i[30] = 0.5f; }
                foreach (var o in outB) Array.Clear(o);
                engine.Process(inB, outB, 128);
                if (hitBlock)
                {
                    float v = outB[0][30];
                    for (int p = 1; p < 4; p++) Assert.Equal(v, outB[p][30]);
                    seen.Add(v);
                }
            }
        }
        cts.Cancel();
        await swapper;
        Assert.Subset(new HashSet<float> { 1f, 0.25f }, seen);
    }

    [Fact]
    public void ApplyKit_WithPresetGainAndDetection_UpdatesSettings()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        var kit = DcKit(16).WithPiece(new PreparedPiece(DrumPiece.Snare, new float[] { 1f }, null, "s")
        {
            GainDb = -3f,
            Detection = new TriggerOverrides { ThresholdDb = -12f },
        });
        engine.ApplyKit(kit);
        Assert.Equal(-3f, engine.Settings[DrumPiece.Snare].OutputGainDb);
        Assert.Equal(-12f, engine.Settings[DrumPiece.Snare].Trigger.ThresholdDb);
        Assert.Equal(KitDefaults.Snare.HighPassHz, engine.Settings[DrumPiece.Snare].Trigger.HighPassHz);
        Assert.Equal(KitSettings.Default[DrumPiece.Kick], engine.Settings[DrumPiece.Kick]);
    }

    [Fact]
    public void ApplyKit_WrongSampleRate_Rejected()
    {
        var engine = new KitEngine(Fs, KitSettings.Default);
        Assert.Throws<ArgumentException>(() => engine.ApplyKit(PreparedKit.Empty(44100)));
    }

    [Fact]
    public void Reset_ClearsVoicesAndPendingTests_OfAllPieces()
    {
        var engine = Engine(AllPieces(Impulsive), DcKit(Fs));
        var inB = Mics(128.0 / Fs);
        var outB = Mics(128.0 / Fs);
        foreach (var i in inB) i[0] = 0.5f;
        engine.Process(inB, outB, 128);
        foreach (var p in DrumPieces.All) Assert.Equal(1, engine.ActiveVoiceCount(p));
        foreach (var p in DrumPieces.All) engine.RequestTest(p);
        engine.Reset();
        foreach (var i in inB) Array.Clear(i);
        foreach (var o in outB) Array.Clear(o);
        engine.Process(inB, outB, 128);
        Assert.All(outB, o => Assert.All(o, v => Assert.Equal(0f, v)));
        while (engine.TryDequeueEvent(out var e)) Assert.False(e.Event.IsManualTest);
    }

    [Fact]
    public void InputPeaks_ArePerPiece()
    {
        var engine = Engine();
        var inB = Mics(128.0 / Fs);
        inB[2][5] = -0.75f;
        engine.Process(inB, Mics(128.0 / Fs), 128);
        Assert.Equal(0f, engine.ReadAndResetInputPeak(DrumPiece.Kick));
        Assert.Equal(0.75f, engine.ReadAndResetInputPeak(DrumPiece.Tom));
        Assert.Equal(0f, engine.ReadAndResetInputPeak(DrumPiece.Tom));
    }

    [Fact]
    public void NullOrShortHostArrays_TreatedAsSilenceAndDiscard()
    {
        var engine = Engine(enabled: false);
        engine.RequestTest(DrumPiece.Snare);
        var inputs = new float[4][];
        inputs[0] = new float[128];
        var outputs = new float[4][];
        outputs[0] = new float[128];
        engine.Process(inputs, outputs, 128);                  // must not throw
        Assert.True(engine.TryDequeueEvent(out var e));
        Assert.Equal(DrumPiece.Snare, e.Piece);
    }

    [Fact]
    public void Process_DoesNotAllocate_WithFourPiecesTestsAndKitSwaps()
    {
        var (mic, _) = SyntheticSignals.MicTrack(Fs);
        var mics = DrumPieces.All.Select(p => mic.Skip((int)Ms(150) * (int)p).Concat(new float[Ms(150) * (int)p]).ToArray()).ToArray();
        var engine = Engine(kit: DcKit(Fs / 4));
        var kitA = DcKit(Fs / 4, 1f);
        var kitB = DcKit(Fs / 4, 0.5f);
        var inB = Mics(128.0 / Fs);
        var outB = Mics(128.0 / Fs);

        long Pass(bool measure)
        {
            long total = 0;
            for (int start = 0, blk = 0; start + 128 <= mic.Length; start += 128, blk++)
            {
                for (int p = 0; p < 4; p++) { Array.Copy(mics[p], start, inB[p], 0, 128); Array.Clear(outB[p]); }
                if (blk % 50 == 0) { engine.ApplyKit(blk % 100 == 0 ? kitA : kitB); engine.RequestTest((DrumPiece)(blk % 4)); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                engine.Process(inB, outB, 128);
                if (measure) total += GC.GetAllocatedBytesForCurrentThread() - before;
                while (engine.TryDequeueEvent(out _)) { }
            }
            return total;
        }

        Pass(measure: false);                    // JIT warm-up of every path
        // Under a loaded parallel test run, tier-up/OSR compilation can still happen on this thread during an early
        // measured pass (a one-time few-KB cost). A real allocation in Process would repeat in EVERY pass (~3000
        // blocks), so require one complete pass with exactly zero bytes.
        var passes = new List<long>();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            passes.Add(Pass(measure: true));
            if (passes[^1] == 0) break;
        }
        Assert.True(passes[^1] == 0, "bytes alocados por passada: " + string.Join(", ", passes));
    }
}
