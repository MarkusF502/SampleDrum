using System.Globalization;
using System.Text;
using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>Timeline of the swap-verification run (seconds of audio).</summary>
public sealed record SwapOptions
{
    public int BlockSize { get; init; } = 256;
    public int Seed { get; init; } = 1;
    /// <summary>Preset applied before audio starts.</summary>
    public string InitialPresetId { get; init; } = "demo-rock";
    /// <summary>Single-piece swap: 10 ms after the hard kick at 0.5 s, so that voice is still ringing.</summary>
    public double SingleSwapAtSec { get; init; } = 0.51;
    public DrumPiece SinglePiece { get; init; } = DrumPiece.Kick;
    public string SingleSampleId { get; init; } = "bumbos/bumbo-seco.wav";
    /// <summary>Whole preset: 50 ms after the 3-piece hit at 7.2 s (three voices ringing), before the 4-piece hit at 7.8 s.</summary>
    public double PresetAtSec { get; init; } = 7.25;
    public string PresetId { get; init; } = "demo-suave";
    /// <summary>Preset with a missing WAV: must change nothing.</summary>
    public double BrokenPresetAtSec { get; init; } = 10.0;
    public string BrokenPresetId { get; init; } = SimulationSampleLibrary.BrokenPresetId;
}

/// <summary>One controller operation issued during the run.</summary>
public sealed record SwapOperationRecord(string Step, string Description, double RequestedAtSec, long RequestedBlock,
    KitOperationSnapshot Result, PreparedKit? KitBefore, PreparedKit? KitAfter);

/// <summary>Everything recorded during a swap run (offline or real time).</summary>
public sealed class SwapRecording
{
    public required KitScenario Scenario { get; init; }
    public required string Mode { get; init; }
    public required int BlockSize { get; init; }
    /// <summary>Return of each piece (index = DrumPiece).</summary>
    public required float[][] ByPiece { get; init; }
    public required IReadOnlyList<KitEvent> Events { get; init; }
    /// <summary>Per processed block: KitEngine.PlayingKit after the block (the kit that block used).</summary>
    public required IReadOnlyList<PreparedKit?> BlockKits { get; init; }
    /// <summary>Per processed block: KitEngine.AppliedKitGeneration after the block.</summary>
    public required IReadOnlyList<long> BlockGenerations { get; init; }
    public required IReadOnlyList<SwapOperationRecord> Operations { get; init; }
    /// <summary>Blocks the host processed (callbacks); must equal BlockKits.Count, no restart.</summary>
    public required long Callbacks { get; init; }
    /// <summary>Host stopped/restarted or faulted during the run (must be false).</summary>
    public bool HostInterrupted { get; init; }
    /// <summary>
    /// True (offline, real time at 1x): the swap must land while an earlier voice of that piece is still ringing, so
    /// "old voice finishes with the old sample" is exercised. Accelerated real-time runs (speed &gt; 1) multiply the
    /// scheduling latency in audio time, so there it is reported but not required; any voice that does cross a swap is
    /// still verified by the residual check.
    /// </summary>
    public bool RequireRingingAtSwap { get; init; } = true;
    public string? HostNote { get; init; }
}

/// <summary>One voice of the run: which sample the engine was expected to use and which one the audio shows.</summary>
public sealed record VoiceRecord(DrumPiece Piece, long PlaybackSample, long Block, string ExpectedSample,
    string IdentifiedSample, double MatchError, bool Ok, string Phase);

public sealed record SwapReport(IReadOnlyList<CheckResult> Checks, IReadOnlyList<VoiceRecord> Voices,
    IReadOnlyList<float> ResidualPeakByPiece)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>
/// Swap verification: plays the 4-piece synthetic scenario through <see cref="KitEngine"/> while a real
/// <see cref="KitController"/> swaps one piece's sample, then a whole preset, then tries a preset with a missing WAV.
/// From the recorded returns it identifies the sample of every voice (sequential reconstruction: audio minus the
/// voices already identified, compared with each candidate sample × the event gain) and checks it against the kit the
/// engine reported for that block. The residual after reconstruction must be silence: a voice cut at the swap (or any
/// wrong audio) leaves a residual.
/// </summary>
public static class SwapHarness
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double MatchLimit = 0.02;   // normalized squared error of the best candidate
    private const float ResidualLimit = 1e-3f; // -60 dBFS

    /// <summary>Engine for the swap run: scenario detection settings, kit and pieces enabled, no samples yet.</summary>
    public static KitEngine CreateEngine(KitScenario sc)
    {
        var engine = new KitEngine(sc.SampleRate, KitHarness.SettingsFor(sc));
        foreach (var p in DrumPieces.All) engine.SetPieceEnabled(p, true);
        engine.KitEnabled = true;
        return engine;
    }

    /// <summary>Deterministic offline run: each operation completes before the next block is processed.</summary>
    public static async Task<SwapRecording> RunOfflineAsync(SimulationSampleLibrary lib, SwapOptions? options = null)
    {
        var o = options ?? new SwapOptions();
        var sc = KitScenario.Generate(o.Seed);
        var engine = CreateEngine(sc);
        await using var controller = new KitController(engine, new SampleLibrary(lib.SamplesFolder));
        var initial = await controller.ApplyPreset(lib.Preset(o.InitialPresetId)).Completion;
        if (initial.Status != KitOperationStatus.Applied)
            throw new InvalidOperationException($"Preset inicial falhou: {string.Join("; ", initial.Errors.Select(e => e.Message))}");

        int block = o.BlockSize, len = sc.Length, n4 = DrumPieces.Count;
        var inputs = Enumerable.Range(0, n4).Select(_ => new float[block]).ToArray();
        var outputs = Enumerable.Range(0, n4).Select(_ => new float[block]).ToArray();
        var byPiece = Enumerable.Range(0, n4).Select(_ => new float[len]).ToArray();
        var kits = new List<PreparedKit?>();
        var gens = new List<long>();
        var events = new List<KitEvent>();
        var ops = new List<SwapOperationRecord>();
        var plan = Plan(lib, o).ToList();
        int next = 0;
        long blocks = 0;
        for (int start = 0; start < len; start += block, blocks++)
        {
            while (next < plan.Count && start >= (long)(plan[next].AtSec * sc.SampleRate))
            {
                var step = plan[next++];
                var before = engine.ActiveKit;
                var op = step.Issue(controller);
                var result = await op.Completion;
                ops.Add(new SwapOperationRecord(step.Step, step.Description, step.AtSec, blocks, result, before, engine.ActiveKit));
            }
            int n = Math.Min(block, len - start);
            for (int p = 0; p < n4; p++) { Array.Copy(sc.Mics[p], start, inputs[p], 0, n); Array.Clear(outputs[p], 0, n); }
            engine.Process(inputs, outputs, n);
            for (int p = 0; p < n4; p++) Array.Copy(outputs[p], 0, byPiece[p], start, n);
            kits.Add(engine.PlayingKit);
            gens.Add(engine.AppliedKitGeneration);
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
        }
        return new SwapRecording
        {
            Scenario = sc, Mode = "offline (determinístico; cada operação termina antes do bloco seguinte)",
            BlockSize = block, ByPiece = byPiece, Events = events, BlockKits = kits, BlockGenerations = gens,
            Operations = ops, Callbacks = blocks,
        };
    }

    /// <summary>A step of the timeline: when, what, and how to issue it on the controller.</summary>
    public sealed record PlannedStep(string Step, string Description, double AtSec, Func<KitController, KitOperation> Issue);

    public static IEnumerable<PlannedStep> Plan(SimulationSampleLibrary lib, SwapOptions o)
    {
        yield return new PlannedStep("peça", $"{o.SinglePiece.Label()} → {o.SingleSampleId}", o.SingleSwapAtSec,
            c => c.SetPieceSample(o.SinglePiece, o.SingleSampleId));
        yield return new PlannedStep("preset", $"preset \"{lib.Preset(o.PresetId).Name}\" ({o.PresetId})", o.PresetAtSec,
            c => c.ApplyPreset(lib.Preset(o.PresetId)));
        yield return new PlannedStep("preset-com-erro", $"preset \"{lib.Preset(o.BrokenPresetId).Name}\" ({o.BrokenPresetId})",
            o.BrokenPresetAtSec, c => c.ApplyPreset(lib.Preset(o.BrokenPresetId)));
    }

    private static string SampleName(PreparedPiece? p) =>
        p?.Samples is null ? "(nenhum)" : p.Name ?? Path.GetFileName(p.SourcePath) ?? "(sem nome)";

    public static SwapReport Analyze(SwapRecording r, SwapOptions? options = null)
    {
        var o = options ?? new SwapOptions();
        var sc = r.Scenario;
        int block = r.BlockSize, w = Math.Max(16, (int)(0.005 * sc.SampleRate));
        var checks = new List<CheckResult>();
        var voices = new List<VoiceRecord>();

        // Candidate samples per piece: every distinct array that some block played.
        var candidates = new List<(float[] Data, string Name)>[DrumPieces.Count];
        for (int p = 0; p < candidates.Length; p++) candidates[p] = [];
        foreach (var kit in r.BlockKits.Where(k => k is not null).Distinct())
            foreach (var pp in kit!.Pieces)
                if (pp.Samples is { } d && !candidates[(int)pp.Piece].Any(c => ReferenceEquals(c.Data, d)))
                    candidates[(int)pp.Piece].Add((d, SampleName(pp)));

        // Sequential identification per piece.
        var residualPeaks = new float[DrumPieces.Count];
        var opsBlocks = r.Operations.Select(op => (op.Step, Block: FirstBlockWith(r, op.KitAfter, op.RequestedBlock))).ToList();
        string PhaseOf(long blk)
        {
            string phase = "inicial";
            foreach (var (step, b) in opsBlocks)
                if (b is long bb && blk >= bb) phase = $"depois: {step}";
            return phase;
        }
        foreach (var piece in DrumPieces.All)
        {
            int pi = (int)piece;
            var outp = r.ByPiece[pi];
            var recon = new float[outp.Length];
            foreach (var e in r.Events.Where(e => e.Piece == piece && !e.Event.IsManualTest).OrderBy(e => e.Event.PlaybackSampleIndex))
            {
                long t = e.Event.PlaybackSampleIndex;
                long blk = Math.Min(t / block, r.BlockKits.Count - 1);
                var expectedPiece = r.BlockKits[(int)blk]?[piece];
                float g = e.Event.Gain;
                (float[] Data, string Name)? best = null;
                double bestErr = double.MaxValue;
                foreach (var c in candidates[pi])
                {
                    double num = 0, den = 0;
                    for (int k = 0; k < w && t + k < outp.Length && k < c.Data.Length; k++)
                    {
                        double ref_ = g * c.Data[k];
                        double d = outp[t + k] - recon[t + k] - ref_;
                        num += d * d;
                        den += ref_ * ref_;
                    }
                    double err = den > 1e-12 ? num / den : double.MaxValue;
                    if (err < bestErr) { bestErr = err; best = c; }
                }
                bool identified = best is not null && bestErr <= MatchLimit;
                if (identified)
                {
                    var d = best!.Value.Data;
                    for (long i = t; i < outp.Length && i - t < d.Length; i++) recon[i] += g * d[i - t];
                }
                bool ok = identified && expectedPiece?.Samples is { } exp && ReferenceEquals(exp, best!.Value.Data);
                voices.Add(new VoiceRecord(piece, t, blk, SampleName(expectedPiece), identified ? best!.Value.Name : "não identificado",
                    bestErr, ok, PhaseOf(blk)));
            }
            float peak = 0f;
            for (int i = 0; i < outp.Length; i++) peak = Math.Max(peak, Math.Abs(outp[i] - recon[i]));
            residualPeaks[pi] = peak;
        }
        voices.Sort((a, b) => a.PlaybackSample != b.PlaybackSample ? a.PlaybackSample.CompareTo(b.PlaybackSample) : a.Piece.CompareTo(b.Piece));

        // ---- checks ----
        var single = r.Operations.FirstOrDefault(op => op.Step == "peça");
        var preset = r.Operations.FirstOrDefault(op => op.Step == "preset");
        var broken = r.Operations.FirstOrDefault(op => op.Step == "preset-com-erro");

        checks.Add(Check("Todas as vozes usaram o sample do kit em vigor no bloco em que começaram (identificado pelo áudio)", () =>
        {
            int bad = voices.Count(v => !v.Ok);
            return (voices.Count > 0 && bad == 0, $"{voices.Count} vozes, {bad} divergentes");
        }));

        checks.Add(Check("Nenhuma voz cortada e nenhum áudio fora do esperado (resíduo da reconstrução < -60 dBFS)", () =>
        {
            float worst = residualPeaks.Max();
            return (worst < ResidualLimit, "resíduo máximo por peça: " + string.Join(", ",
                DrumPieces.All.Select(p => $"{p.Label()} {Db(residualPeaks[(int)p])}")));
        }));

        checks.Add(Check($"Troca de uma peça ({o.SinglePiece.Label()}): 1º golpe depois usa o sample novo; voz anterior termina com o antigo", () =>
        {
            if (single is null || single.Result.Status != KitOperationStatus.Applied)
                return (false, $"operação: {single?.Result.Status.ToString() ?? "não executada"}");
            long? sb = FirstBlockWith(r, single.KitAfter, single.RequestedBlock);
            if (sb is null) return (false, "o kit novo nunca tocou");
            var pv = voices.Where(v => v.Piece == o.SinglePiece).ToList();
            var before = pv.Where(v => v.Block < sb).ToList();
            var after = pv.FirstOrDefault(v => v.Block >= sb);
            string oldName = SampleName(single.KitBefore?[o.SinglePiece]), newName = SampleName(single.KitAfter?[o.SinglePiece]);
            var ringing = before.Where(v => v.PlaybackSample + (single.KitBefore?[o.SinglePiece].Samples?.Length ?? 0) > sb * block).ToList();
            bool ok = after is not null && after.IdentifiedSample == newName && after.Ok
                      && before.All(v => v.IdentifiedSample == oldName && v.Ok)
                      && (ringing.Count > 0 || !r.RequireRingingAtSwap) && residualPeaks[(int)o.SinglePiece] < ResidualLimit;
            return (ok, $"troca no bloco {sb} ({Sec(sb.Value * block, sc)}); antes: {before.Count} voz(es) com {oldName} " +
                        $"({ringing.Count} ainda soando na troca, sem corte{(r.RequireRingingAtSwap ? "" : "; voz soando não exigida: rodada acelerada")}); " +
                        $"1º golpe depois: {after?.IdentifiedSample ?? "nenhum"} " +
                        $"em {(after is null ? "—" : Sec(after.PlaybackSample, sc))} (esperado {newName})");
        }));

        checks.Add(Check("Troca de uma peça não muda as outras três", () =>
        {
            if (single?.KitBefore is null || single.KitAfter is null) return (false, "sem kits antes/depois");
            var others = DrumPieces.All.Where(p => p != o.SinglePiece).ToList();
            bool same = others.All(p => ReferenceEquals(single.KitBefore[p].Samples, single.KitAfter[p].Samples));
            long? sb = FirstBlockWith(r, single.KitAfter, single.RequestedBlock);
            long end = preset is null ? long.MaxValue : FirstBlockWith(r, preset.KitAfter, preset.RequestedBlock) ?? long.MaxValue;
            var vs = voices.Where(v => others.Contains(v.Piece) && v.Block < end).ToList();
            bool voicesSame = vs.All(v => v.Ok && v.IdentifiedSample == SampleName(single.KitBefore[v.Piece]));
            return (same && voicesSame, $"amostras das outras peças idênticas (mesma referência): {same}; " +
                                        $"{vs.Count} golpes delas até o preset, todos com o sample de antes: {voicesSame}");
        }));

        checks.Add(Check("Preset troca as 4 peças no MESMO limite de bloco (geração do kit por bloco)", () =>
        {
            if (preset is null || preset.Result.Status != KitOperationStatus.Applied) return (false, $"operação: {preset?.Result.Status.ToString() ?? "não executada"}");
            long? pb = FirstBlockWith(r, preset.KitAfter, preset.RequestedBlock);
            if (pb is null || pb == 0) return (false, "o kit do preset nunca tocou");
            int b = (int)pb.Value;
            var prev = r.BlockKits[b - 1];
            var cur = r.BlockKits[b];
            bool allChanged = DrumPieces.All.All(p => !ReferenceEquals(prev?[p].Samples, cur?[p].Samples));
            bool oneStep = r.BlockGenerations[b] > r.BlockGenerations[b - 1];
            // between the request and the swap block every block still played the old kit (no partial kit)
            bool noPartial = Enumerable.Range((int)preset.RequestedBlock, Math.Max(0, b - (int)preset.RequestedBlock))
                .All(i => ReferenceEquals(r.BlockKits[i], preset.KitBefore) || ReferenceEquals(r.BlockKits[i], prev));
            var firstHits = DrumPieces.All.Select(p => voices.FirstOrDefault(v => v.Piece == p && v.Block >= b)).ToList();
            bool hitsNew = firstHits.All(v => v is not null && v.Ok && v.IdentifiedSample == SampleName(preset.KitAfter?[v.Piece]));
            return (allChanged && oneStep && noPartial && hitsNew,
                $"bloco {b} ({Sec((long)b * block, sc)}): as 4 peças mudaram juntas: {allChanged}; geração {r.BlockGenerations[b - 1]} → {r.BlockGenerations[b]}; " +
                $"nenhum bloco com kit parcial: {noPartial}; 1º golpe de cada peça depois: " +
                string.Join(", ", firstHits.Select((v, i) => $"{((DrumPiece)i).Label()} {v?.IdentifiedSample ?? "—"}")));
        }));

        checks.Add(Check("Preset com WAV faltando não muda nada e informa o erro por peça", () =>
        {
            if (broken is null) return (false, "não executado");
            var errs = broken.Result.Errors;
            bool failed = broken.Result.Status == KitOperationStatus.Failed;
            bool hasCode = errs.Any(e => e.Piece == DrumPiece.Snare && e.Code == PieceLoadErrorCode.FileNotFound);
            bool unchanged = ReferenceEquals(broken.KitBefore, broken.KitAfter)
                             && r.BlockKits.Skip((int)broken.RequestedBlock).All(k => ReferenceEquals(k, broken.KitBefore));
            long g0 = broken.RequestedBlock > 0 ? r.BlockGenerations[(int)broken.RequestedBlock - 1] : 0;
            bool genSame = r.BlockGenerations.Skip((int)broken.RequestedBlock).All(g => g == g0);
            return (failed && hasCode && unchanged && genSame,
                $"status {broken.Result.Status}; erros: {string.Join("; ", errs.Select(e => $"{e.Piece.Label()} {e.Code}: {e.Message}"))}; " +
                $"kit igual até o fim: {unchanged}; geração sem mudar ({g0}): {genSame}");
        }));

        checks.Add(Check("Sem reinício do áudio: blocos contínuos do começo ao fim", () =>
        {
            long expected = (sc.Length + block - 1) / block;
            bool ok = !r.HostInterrupted && r.Callbacks == r.BlockKits.Count && r.BlockKits.Count >= expected;
            return (ok, $"{r.Callbacks} blocos processados (esperado ≥ {expected}), registros por bloco {r.BlockKits.Count}, " +
                        $"host interrompido: {r.HostInterrupted}{(r.HostNote is null ? "" : $" ({r.HostNote})")}");
        }));

        return new SwapReport(checks, voices, residualPeaks);
    }

    private static long? FirstBlockWith(SwapRecording r, PreparedKit? kit, long fromBlock)
    {
        if (kit is null) return null;
        for (int i = (int)Math.Max(0, fromBlock); i < r.BlockKits.Count; i++)
            if (ReferenceEquals(r.BlockKits[i], kit)) return i;
        return null;
    }

    private static CheckResult Check(string name, Func<(bool, string)> f) => HarnessChecks.Safe(name, f);
    private static string Db(float lin) => lin <= 0 ? "-∞ dBFS" : $"{20 * Math.Log10(lin):0.0} dBFS".Replace(',', '.');
    private static string Sec(long sample, KitScenario sc) => (sample / (double)sc.SampleRate).ToString("0.000", Inv) + " s";

    /// <summary>Writes relatorio-trocas.md, relatorio-trocas.csv (one row per voice) and retornos-4ch.wav.</summary>
    public static IReadOnlyList<string> WriteFiles(SwapRecording r, SwapReport rep, string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        string F(string name) { var p = Path.Combine(dir, name); files.Add(p); return p; }
        MultiChannelWav.Write(F("retornos-4ch.wav"), r.ByPiece, r.Scenario.SampleRate);

        var csv = new StringBuilder("peca,inicio_amostra,inicio_s,bloco,fase,sample_esperado,sample_identificado,erro_normalizado,status\n");
        foreach (var v in rep.Voices)
            csv.AppendLine(string.Join(",", v.Piece.Label(), v.PlaybackSample.ToString(Inv),
                (v.PlaybackSample / (double)r.Scenario.SampleRate).ToString("0.000", Inv), v.Block.ToString(Inv),
                "\"" + v.Phase + "\"", v.ExpectedSample, v.IdentifiedSample, v.MatchError.ToString("0.0000", Inv), v.Ok ? "ok" : "divergente"));
        File.WriteAllText(F("relatorio-trocas.csv"), csv.ToString(), new UTF8Encoding(true));

        var md = new StringBuilder();
        md.AppendLine("# SampleDrum — verificação de trocas de sample durante o áudio").AppendLine();
        md.AppendLine($"- Modo: {r.Mode}; bloco {r.BlockSize} amostras @ {r.Scenario.SampleRate} Hz; cenário sintético semente {r.Scenario.Seed} ({r.Scenario.Length / (double)r.Scenario.SampleRate:0.0} s)");
        md.AppendLine("- Biblioteca de demonstração (WAVs gerados) e `KitController` real; o sample de cada voz é identificado no áudio gravado de cada retorno.");
        md.AppendLine().AppendLine($"**Resultado geral: {(rep.Passed ? "OK" : "FALHOU")}**").AppendLine();
        md.AppendLine("## Operações").AppendLine();
        md.AppendLine("| Etapa | Pedido em | Bloco do pedido | Status | Erros |");
        md.AppendLine("|---|---:|---:|---|---|");
        foreach (var op in r.Operations)
            md.AppendLine($"| {op.Step}: {op.Description} | {op.RequestedAtSec.ToString("0.000", Inv)} s | {op.RequestedBlock} | {op.Result.Status} | " +
                          $"{(op.Result.Errors.Count == 0 ? "—" : string.Join("; ", op.Result.Errors.Select(e => $"{e.Piece.Label()} {e.Code}")))} |");
        md.AppendLine().AppendLine("## Verificações").AppendLine();
        foreach (var c in rep.Checks) md.AppendLine($"- [{(c.Passed ? "OK" : "FALHA")}] **{c.Name}** — {c.Detail}");
        md.AppendLine().AppendLine("## Vozes").AppendLine();
        md.AppendLine("| Peça | Início (s) | Bloco | Fase | Sample esperado (kit do bloco) | Sample no áudio | Erro | Status |");
        md.AppendLine("|---|---:|---:|---|---|---|---:|---|");
        foreach (var v in rep.Voices)
            md.AppendLine($"| {v.Piece.Label()} | {(v.PlaybackSample / (double)r.Scenario.SampleRate).ToString("0.000", Inv)} | {v.Block} | {v.Phase} | " +
                          $"{v.ExpectedSample} | {v.IdentifiedSample} | {v.MatchError.ToString("0.0000", Inv)} | {(v.Ok ? "ok" : "divergente")} |");
        File.WriteAllText(F("relatorio-trocas.md"), md.ToString(), new UTF8Encoding(true));
        return files;
    }
}
