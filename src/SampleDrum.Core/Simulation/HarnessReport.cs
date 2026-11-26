using System.Globalization;
using System.Text;

namespace SampleDrum.Core.Simulation;

/// <summary>Result status of one report row.</summary>
public enum HarnessStatus
{
    /// <summary>Expected trigger found on the right output, or a must-not-trigger expectation that indeed did not trigger.</summary>
    Ok,
    /// <summary>Expected trigger not detected.</summary>
    Missing,
    /// <summary>Detected trigger that was not expected (bleed, double inside lockout, or unexplained).</summary>
    Extra,
    /// <summary>Trigger detected, but its sample did not come out on the piece's routed output.</summary>
    WrongOutput,
}

/// <summary>A detection reported by the engine for one piece (piece-independent copy of KitEvent/TriggerEvent).</summary>
public readonly record struct DetectedHit(int Piece, long SampleIndex, long PlaybackSampleIndex, float Peak, float Velocity,
    float Gain, bool IsRetrigger);

/// <summary>One row of the harness report.</summary>
public sealed record HarnessRow(
    int Piece, int? InputDesk, int? OutputDesk,
    long? ExpectedSample, long? DetectedSample, long? ErrorSamples, double? ErrorMs,
    float? Velocity, HarnessStatus Status, bool ExpectTrigger, string Label, int? DetectedOutputDesk);

/// <summary>Per-piece totals.</summary>
public sealed record PieceSummary(int Piece, int? InputDesk, int? OutputDesk, int ExpectedTriggers, int ExpectedSilent,
    int Detected, int Ok, int Missing, int Extra, int WrongOutput, double MeanAbsErrorMs, double MaxAbsErrorMs)
{
    public bool Passed => Missing == 0 && Extra == 0 && WrongOutput == 0;
}

/// <summary>Harness report: rows + per-piece totals, CSV and Markdown writers. Pass = no missing/extra/wrong-output.</summary>
public sealed class HarnessReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public HarnessReport(IReadOnlyList<HarnessRow> rows, IReadOnlyList<PieceSummary> summaries, int sampleRate, bool hasExpectations)
    {
        Rows = rows; Summaries = summaries; SampleRate = sampleRate; HasExpectations = hasExpectations;
    }

    public IReadOnlyList<HarnessRow> Rows { get; }
    public IReadOnlyList<PieceSummary> Summaries { get; }
    public int SampleRate { get; }
    /// <summary>False when the mics came from user WAVs (no expectations: detections are only listed).</summary>
    public bool HasExpectations { get; }
    public bool Passed => HasExpectations && Summaries.All(s => s.Passed);

    public static string StatusText(HarnessStatus s) => s switch
    {
        HarnessStatus.Ok => "ok",
        HarnessStatus.Missing => "faltando",
        HarnessStatus.Extra => "extra",
        HarnessStatus.WrongOutput => "saída-errada",
        _ => s.ToString(),
    };

    private static string Desk(int? ch) => ch is null ? "pendente" : $"USB {ch}";

    public void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("peca,entrada,saida,esperado_amostra,detectado_amostra,erro_amostras,erro_ms,velocidade,status,deve_disparar,descricao,saida_detectada");
        foreach (var r in Rows)
        {
            sb.AppendLine(string.Join(",",
                SyntheticKit.PieceNames[r.Piece], Desk(r.InputDesk), Desk(r.OutputDesk),
                r.ExpectedSample?.ToString(Inv) ?? "", r.DetectedSample?.ToString(Inv) ?? "",
                r.ErrorSamples?.ToString(Inv) ?? "", r.ErrorMs?.ToString("0.000", Inv) ?? "",
                r.Velocity?.ToString("0.000", Inv) ?? "", StatusText(r.Status), r.ExpectTrigger ? "sim" : "nao",
                "\"" + r.Label.Replace("\"", "'") + "\"", r.DetectedOutputDesk is null ? "" : $"USB {r.DetectedOutputDesk}"));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Per-piece summary as a Markdown table (also printed on the console).</summary>
    public string SummaryTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Peça | Entrada | Saída | Esperados | Silêncios esperados | Detectados | ok | faltando | extra | saída-errada | erro médio (ms) | erro máx (ms) | Resultado |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var s in Summaries)
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10:0.00} | {11:0.00} | {12} |",
                SyntheticKit.PieceNames[s.Piece], Desk(s.InputDesk), Desk(s.OutputDesk), s.ExpectedTriggers, s.ExpectedSilent,
                s.Detected, s.Ok, s.Missing, s.Extra, s.WrongOutput, s.MeanAbsErrorMs, s.MaxAbsErrorMs,
                !HasExpectations ? "sem gabarito" : s.Passed ? "OK" : "FALHOU"));
        return sb.ToString();
    }

    public void WriteMarkdown(string path, string title, IEnumerable<string> headerLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        foreach (var l in headerLines) sb.AppendLine($"- {l}");
        sb.AppendLine().AppendLine($"**Resultado geral: {(!HasExpectations ? "sem gabarito (microfones do usuário)" : Passed ? "OK" : "FALHOU")}**").AppendLine();
        sb.AppendLine("## Resumo por peça").AppendLine().Append(SummaryTable()).AppendLine();
        sb.AppendLine("## Eventos").AppendLine();
        sb.AppendLine("| Peça | Entrada | Saída | Esperado (amostra) | Detectado (amostra) | Erro (amostras) | Erro (ms) | Velocidade | Status | Descrição |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var r in Rows)
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |",
                SyntheticKit.PieceNames[r.Piece], Desk(r.InputDesk), Desk(r.OutputDesk),
                r.ExpectedSample?.ToString(Inv) ?? "—", r.DetectedSample?.ToString(Inv) ?? "—",
                r.ErrorSamples?.ToString(Inv) ?? "—", r.ErrorMs?.ToString("0.000", Inv) ?? "—",
                r.Velocity?.ToString("0.00", Inv) ?? "—",
                StatusText(r.Status) + (r.Status == HarnessStatus.WrongOutput && r.DetectedOutputDesk is not null ? $" (saiu em USB {r.DetectedOutputDesk})" : ""),
                r.Label));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}

/// <summary>
/// Compares engine detections and the routed returns with a scenario's expectations.
/// Matching is per piece, greedy in time order, within ±tolerance. Output check: right after each matched trigger's
/// playback start, the piece's routed output must carry audio; if it is silent while another output starts, the row is
/// <see cref="HarnessStatus.WrongOutput"/>.
/// </summary>
public static class ScenarioEvaluator
{
    public const double DefaultToleranceMs = 5.0;
    private const float AudibleLin = 1e-3f; // -60 dBFS

    /// <param name="scenario">Scenario with the mic tracks and expectations.</param>
    /// <param name="detections">Detections of every piece.</param>
    /// <param name="physicalOutputs">Routed return audio keyed by desk output channel (USB n).</param>
    /// <param name="inputDesk">Per piece: desk input channel (null = pending).</param>
    /// <param name="outputDesk">Per piece: desk output channel (null = pending).</param>
    /// <param name="toleranceMs">Matching tolerance (ms).</param>
    public static HarnessReport Evaluate(KitScenario scenario, IReadOnlyList<DetectedHit> detections,
        IReadOnlyDictionary<int, float[]> physicalOutputs, IReadOnlyList<int?> inputDesk, IReadOnlyList<int?> outputDesk,
        double toleranceMs = DefaultToleranceMs)
    {
        int fs = scenario.SampleRate;
        long tol = (long)Math.Round(toleranceMs * 0.001 * fs);
        bool hasExpectations = scenario.Expected.Count > 0;
        var rows = new List<HarnessRow>();
        var summaries = new List<PieceSummary>();

        for (int p = 0; p < SyntheticKit.PieceCount; p++)
        {
            var det = detections.Where(d => d.Piece == p).OrderBy(d => d.SampleIndex).ToList();
            var expTrig = scenario.Expected.Where(e => e.Piece == p && e.ExpectTrigger).OrderBy(e => e.SampleIndex).ToList();
            var expSilent = scenario.Expected.Where(e => e.Piece == p && !e.ExpectTrigger).OrderBy(e => e.SampleIndex).ToList();
            var matches = OnsetMatcher.Match(expTrig.Select(e => e.SampleIndex).ToList(), det.Select(d => d.SampleIndex).ToList(), tol);
            var pieceRows = new List<HarnessRow>();
            var usedDetections = new HashSet<int>();

            foreach (var m in matches.Where(m => m.ExpectedIndex >= 0))
            {
                var e = expTrig[m.ExpectedIndex];
                if (m.IsMiss)
                {
                    pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, null, null, null, null,
                        HarnessStatus.Missing, true, e.Label, null));
                    continue;
                }
                usedDetections.Add(m.DetectedIndex);
                var d = det[m.DetectedIndex];
                var (status, seenOn) = CheckOutput(d, outputDesk[p], physicalOutputs, fs);
                pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, d.SampleIndex, m.ErrorSamples,
                    m.ErrorSamples * 1000.0 / fs, d.Velocity, status, true, e.Label, seenOn));
            }

            // Must-not-trigger expectations: a detection near one of them is an Extra with that explanation.
            foreach (var e in expSilent)
            {
                int hit = -1;
                for (int i = 0; i < det.Count; i++)
                    if (!usedDetections.Contains(i) && Math.Abs(det[i].SampleIndex - e.SampleIndex) <= tol * 4) { hit = i; break; }
                if (hit < 0)
                {
                    pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, null, null, null, null,
                        HarnessStatus.Ok, false, e.Label, null));
                }
                else
                {
                    usedDetections.Add(hit);
                    var d = det[hit];
                    pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, d.SampleIndex,
                        d.SampleIndex - e.SampleIndex, (d.SampleIndex - e.SampleIndex) * 1000.0 / fs, d.Velocity,
                        HarnessStatus.Extra, false, e.Label, null));
                }
            }

            for (int i = 0; i < det.Count; i++)
            {
                if (usedDetections.Contains(i)) continue;
                var d = det[i];
                pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], null, d.SampleIndex, null, null, d.Velocity,
                    hasExpectations ? HarnessStatus.Extra : HarnessStatus.Ok, false,
                    hasExpectations ? "disparo inesperado" : (d.IsRetrigger ? "detecção (redisparo)" : "detecção"), null));
            }

            pieceRows.Sort((a, b) => (a.ExpectedSample ?? a.DetectedSample ?? 0).CompareTo(b.ExpectedSample ?? b.DetectedSample ?? 0));
            rows.AddRange(pieceRows);

            var errs = pieceRows.Where(r => r.ExpectTrigger && r.ErrorMs is not null).Select(r => Math.Abs(r.ErrorMs!.Value)).ToList();
            summaries.Add(new PieceSummary(p, inputDesk[p], outputDesk[p], expTrig.Count, expSilent.Count, det.Count,
                pieceRows.Count(r => r.Status == HarnessStatus.Ok),
                pieceRows.Count(r => r.Status == HarnessStatus.Missing),
                pieceRows.Count(r => r.Status == HarnessStatus.Extra),
                pieceRows.Count(r => r.Status == HarnessStatus.WrongOutput),
                errs.Count > 0 ? errs.Average() : 0, errs.Count > 0 ? errs.Max() : 0));
        }
        rows.Sort((a, b) => (a.ExpectedSample ?? a.DetectedSample ?? 0).CompareTo(b.ExpectedSample ?? b.DetectedSample ?? 0));
        return new HarnessReport(rows, summaries, fs, hasExpectations);
    }

    private static (HarnessStatus, int?) CheckOutput(DetectedHit d, int? outDesk, IReadOnlyDictionary<int, float[]> outputs, int fs)
    {
        int win = Math.Max(1, (int)(0.003 * fs));
        long start = d.PlaybackSampleIndex;
        if (outDesk is int o && outputs.TryGetValue(o, out var buf) && Peak(buf, start, win) > AudibleLin)
            return (HarnessStatus.Ok, o);
        // Expected output silent (or pending): find an output that starts playing here.
        foreach (var (ch, b) in outputs)
        {
            if (ch == outDesk) continue;
            float after = Peak(b, start, win), before = Peak(b, start - win, win);
            if (after > AudibleLin && after > 4 * before) return (HarnessStatus.WrongOutput, ch);
        }
        return (HarnessStatus.WrongOutput, null);
    }

    private static float Peak(float[] b, long start, int len)
    {
        float m = 0f;
        for (long i = Math.Max(0, start); i < Math.Min(b.Length, start + len); i++) m = Math.Max(m, Math.Abs(b[i]));
        return m;
    }
}
