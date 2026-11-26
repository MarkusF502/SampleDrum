using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>Result of one harness check (pt-BR name and detail).</summary>
public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// Automated harness checks shared by the xUnit tests (tests/SampleDrum.Core.Tests/Harness*.cs) and
/// <c>SampleDrum.App.exe --selftest</c>, so both run exactly the same logic. Offline and deterministic; they drive the
/// real <see cref="KitEngine"/> through <see cref="KitHarness"/> with the "Simulação" routing profile.
/// Exceptions (e.g. a Core member not implemented yet) become a failed check with the message.
/// </summary>
public static class HarnessChecks
{
    public static readonly int[] BlockSizes = [64, 128, 256, 480];

    /// <summary>All offline checks. <paramref name="tempDir"/> is used for the settings-migration check.</summary>
    public static IReadOnlyList<CheckResult> RunAll(string tempDir) =>
    [
        Safe("Cada entrada dispara a peça e a saída esperadas (perfil Simulação)", RoutingPerPiece),
        Safe("Duas ou mais peças disparam juntas na mesma amostra", Simultaneous),
        Safe("Desativar uma peça silencia só o retorno dela", DisableOnePiece),
        Safe("Teste manual de cada peça chega só na saída dela", ManualTestPerPiece),
        Safe("Batidas que atravessam a fronteira de bloco (64/128/256/480) na mesma posição", BlockCrossing),
        Safe("Vazamento (bumbo→surdo, caixa→tom) não dispara a peça errada", BleedDoesNotTrigger),
        Safe("Troca de kit (ApplyKit) no meio da execução: vozes antigas terminam, novas usam o kit novo", ApplyKitMidRun),
        Safe("Migração do settings.json v1 mantém o perfil do bumbo", () => MigrationKeepsKick(tempDir)),
    ];

    public static CheckResult Safe(string name, Func<(bool ok, string detail)> check)
    {
        try
        {
            var (ok, detail) = check();
            return new CheckResult(name, ok, detail);
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static HarnessResult Run(KitScenario sc, int block = 256, KitEngine? engine = null)
        => KitHarness.RunOffline(sc, new HarnessOptions { BlockSize = block }, engine);

    public static (bool, string) RoutingPerPiece()
    {
        var sc = KitScenario.Generate(1);
        var r = Run(sc);
        var routed = r.Routing.RoutedPieces.Count;
        var parts = r.Report.Summaries.Select(s =>
            $"{SyntheticKit.PieceNames[s.Piece]} USB {s.InputDesk}→USB {s.OutputDesk}: ok {s.Ok}, faltando {s.Missing}, extra {s.Extra}, saída-errada {s.WrongOutput}");
        bool ok = routed == 4 && r.Report.Passed && r.Report.Summaries.All(s => s.Detected > 0);
        return (ok, $"{routed} peças roteadas; " + string.Join("; ", parts));
    }

    public static (bool, string) Simultaneous()
    {
        var sc = KitScenario.Generate(1);
        var r = Run(sc);
        var groups = sc.Expected.Where(e => e.ExpectTrigger && e.Label.StartsWith("simultânea", StringComparison.Ordinal))
            .GroupBy(e => e.SampleIndex).ToList();
        var details = new List<string>();
        bool ok = groups.Count == 3;
        foreach (var g in groups)
        {
            int expected = g.Count();
            int got = g.Count(e => r.Report.Rows.Any(row => row.Piece == e.Piece && row.ExpectedSample == e.SampleIndex
                                                           && row.Status == HarnessStatus.Ok));
            details.Add($"{expected} peças na amostra {g.Key}: {got} disparos corretos");
            ok &= got == expected;
        }
        return (ok, string.Join("; ", details));
    }

    public static (bool, string) DisableOnePiece()
    {
        var sc = KitScenario.Generate(1);
        var baseline = Run(sc);
        var engine = KitHarness.CreateEngine(sc);
        engine.SetPieceEnabled(DrumPiece.Tom, false);
        var r = Run(sc, engine: engine);
        float tomPeak = Peak(r.ReturnsByPiece[(int)DrumPiece.Tom]);
        bool othersSame = DrumPieces.All.Where(p => p != DrumPiece.Tom)
            .All(p => r.ReturnsByPiece[(int)p].AsSpan().SequenceEqual(baseline.ReturnsByPiece[(int)p]));
        int tomEvents = r.Events.Count(e => e.Piece == DrumPiece.Tom && !e.Event.IsManualTest);
        bool ok = tomPeak == 0f && othersSame && tomEvents == 0 && Peak(baseline.ReturnsByPiece[(int)DrumPiece.Tom]) > 0.1f;
        return (ok, $"Tom desativado: pico do retorno {tomPeak}, eventos {tomEvents}; outras peças idênticas à execução normal: {othersSame}");
    }

    public static (bool, string) ManualTestPerPiece()
    {
        var sc = KitScenario.Generate(1);
        var details = new List<string>();
        bool ok = true;
        foreach (var p in DrumPieces.All)
        {
            var engine = KitHarness.CreateEngine(sc);
            engine.KitEnabled = false; // manual test must work even with the kit disabled
            engine.RequestTest(p);
            var silence = KitScenario.FromMics(Enumerable.Range(0, 4).Select(_ => new float[sc.SampleRate / 2]).ToArray());
            var r = KitHarness.RunOffline(silence, new HarnessOptions { BlockSize = 256 }, engine);
            int expectedOut = r.Routing.OutputIndex(p)!.Value + 1;
            var loud = r.PhysicalOutputs.Where(kv => Peak(kv.Value) > 1e-3f).Select(kv => kv.Key).ToList();
            var ev = r.Events.Where(e => e.Event.IsManualTest).Select(e => e.Piece).ToList();
            bool pieceOk = loud.SequenceEqual([expectedOut]) && ev.SequenceEqual([p]);
            details.Add($"{p.Label()}: saídas com áudio [{string.Join(",", loud.Select(c => $"USB {c}"))}] (esperado USB {expectedOut}), eventos de teste [{string.Join(",", ev.Select(x => x.Label()))}]");
            ok &= pieceOk;
        }
        return (ok, string.Join("; ", details));
    }

    public static (bool, string) BlockCrossing()
    {
        var sc = KitScenario.Generate(1);
        var crossing = sc.Expected.Where(e => e.Label.StartsWith("atravessa", StringComparison.Ordinal)).ToList();
        var details = new List<string>();
        bool ok = crossing.Count == 4;
        List<long>? reference = null;
        foreach (int b in BlockSizes)
        {
            var r = Run(sc, b);
            var rows = crossing.Select(e => r.Report.Rows.First(row => row.Piece == e.Piece && row.ExpectedSample == e.SampleIndex)).ToList();
            bool allOk = rows.All(row => row.Status == HarnessStatus.Ok);
            var positions = r.Events.Where(e => !e.Event.IsManualTest).Select(e => e.Event.SampleIndex).OrderBy(x => x).ToList();
            reference ??= positions;
            bool same = positions.SequenceEqual(reference);
            details.Add($"bloco {b}: {rows.Count(x => x.Status == HarnessStatus.Ok)}/{rows.Count} ok, posições iguais às do bloco {BlockSizes[0]}: {same}");
            ok &= allOk && same && r.Report.Passed;
        }
        return (ok, string.Join("; ", details));
    }

    public static (bool, string) BleedDoesNotTrigger()
    {
        var sc = KitScenario.Generate(1);
        var r = Run(sc);
        var bleedRows = r.Report.Rows.Where(row => !row.ExpectTrigger && row.Label.StartsWith("vazamento", StringComparison.Ordinal)).ToList();
        int bad = bleedRows.Count(row => row.Status != HarnessStatus.Ok);
        bool ok = bleedRows.Count > 0 && bad == 0;
        return (ok, $"{bleedRows.Count} pontos de vazamento verificados, {bad} disparos indevidos");
    }

    public static (bool, string) ApplyKitMidRun()
    {
        var sc = KitScenario.Generate(1);
        var engine = KitHarness.CreateEngine(sc);
        var kitB = new PreparedKit("Kit B (polaridade invertida)", sc.SampleRate, DrumPieces.All.Select(p =>
            new PreparedPiece(p, sc.Samples[(int)p].Select(v => -v).ToArray(), null, "B")));

        // Swap right after a kick hit started (its voice must keep kit A), before the next kick hit.
        var kicks = sc.Expected.Where(e => e.Piece == 0 && e.ExpectTrigger).Select(e => e.SampleIndex).ToList();
        long swapAt = kicks[0] + sc.SampleRate / 100; // 10 ms after the first kick
        int block = 256;
        var inputs = Enumerable.Range(0, 4).Select(_ => new float[block]).ToArray();
        var outputs = Enumerable.Range(0, 4).Select(_ => new float[block]).ToArray();
        var kickOut = new float[sc.Length];
        var events = new List<KitEvent>();
        bool swapped = false;
        for (int start = 0; start < sc.Length; start += block)
        {
            if (!swapped && start >= swapAt) { engine.ApplyKit(kitB); swapped = true; }
            int n = Math.Min(block, sc.Length - start);
            for (int p = 0; p < 4; p++) { Array.Copy(sc.Mics[p], start, inputs[p], 0, n); Array.Clear(outputs[p], 0, n); }
            engine.Process(inputs, outputs, n);
            Array.Copy(outputs[0], 0, kickOut, start, n);
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
        }
        var kickEvents = events.Where(e => e.Piece == DrumPiece.Kick && !e.Event.IsManualTest).ToList();
        if (kickEvents.Count < 2) return (false, $"só {kickEvents.Count} disparos do bumbo");
        var a = sc.Samples[0];
        double Corr(long at, int from, int len)
        {
            double s = 0;
            for (int k = from; k < from + len && at + k < kickOut.Length && k < a.Length; k++) s += kickOut[at + k] * a[k];
            return s;
        }
        var first = kickEvents[0].Event;
        int w = sc.SampleRate / 200; // 5 ms
        double firstHead = Corr(first.PlaybackSampleIndex, 0, w);
        // Tail of the first voice after the swap: still kit A (positive correlation with A).
        int tailFrom = (int)(swapAt - first.PlaybackSampleIndex) + block;
        double firstTail = Corr(first.PlaybackSampleIndex, tailFrom, w);
        var second = kickEvents[1].Event;
        double secondHead = Corr(second.PlaybackSampleIndex, 0, w);
        bool active = ReferenceEquals(engine.ActiveKit, kitB);
        bool ok = firstHead > 0 && firstTail > 0 && secondHead < 0 && active;
        return (ok, $"1º bumbo (kit A) correlação início {firstHead:0.###}, cauda após a troca {firstTail:0.###}; " +
                    $"2º bumbo (kit B) {secondHead:0.###}; ActiveKit = kit B: {active}");
    }

    public static (bool, string) MigrationKeepsKick(string tempDir)
    {
        string dir = Path.Combine(tempDir, "sampledrum-migracao-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, AppConfig.FileName);
            const string v1 = """
            {
              "SettingsVersion": 1, "DriverName": "X-USB ASIO Driver", "UseSimulation": false,
              "InputDeskChannel": 5, "OutputDeskChannel": 2, "SamplePath": "C:\\samples\\bumbo.wav",
              "AdvancedExpanded": true, "ThresholdDb": -18, "RearmDb": -26, "MinIntervalMs": 60, "HighPassHz": 55,
              "FixedVelocity": true
            }
            """;
            File.WriteAllText(path, v1);
            var cfg = AppConfig.Load(path, out var message);
            var kick = cfg.ToKitSettings()[DrumPiece.Kick].Trigger;
            var church = cfg.Profiles.First(p => p.Name == RoutingProfiles.ChurchName);
            bool backup = File.Exists(Path.Combine(dir, AppConfig.V1BackupFileName));
            bool othersPending = DrumPieces.All.Where(p => p != DrumPiece.Kick).All(p => church[p].IsPending);
            bool ok = cfg.SettingsVersion == AppConfig.CurrentVersion
                      && kick.ThresholdDb == -18f && kick.RearmDb == -26f && kick.MinIntervalMs == 60f
                      && kick.HighPassHz == 55f && kick.FixedVelocity
                      && church[DrumPiece.Kick] == new PieceRoute(5, 2) && othersPending
                      && cfg[DrumPiece.Kick].SamplePath == @"C:\samples\bumbo.wav"
                      && cfg.DriverName == "X-USB ASIO Driver" && backup;
            return (ok, $"versão {cfg.SettingsVersion}; bumbo limiar {kick.ThresholdDb}, rearme {kick.RearmDb}, intervalo {kick.MinIntervalMs}, " +
                        $"passa-altas {kick.HighPassHz}, velocidade fixa {kick.FixedVelocity}; rota igreja bumbo {church[DrumPiece.Kick]}; " +
                        $"outras pendentes {othersPending}; backup v1 {backup}; mensagem: {message}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private static float Peak(float[] b)
    {
        float m = 0f;
        foreach (var v in b) m = Math.Max(m, Math.Abs(v));
        return m;
    }
}
