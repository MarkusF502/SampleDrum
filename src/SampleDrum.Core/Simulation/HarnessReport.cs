using System.Globalization;
using System.Text;

namespace SampleDrum.Core.Simulation;

/// <summary>Result status of one report row.</summary>
public enum HarnessStatus
{
    /// <summary>Expected trigger found and its sample started on the right output only, or a must-not-trigger
    /// expectation that indeed did not trigger.</summary>
    Ok,
    /// <summary>Expected trigger not detected.</summary>
    Missing,
    /// <summary>Trigger that was not expected (bleed, double inside lockout, unexplained), or audio that starts on an
    /// output without any trigger explaining it.</summary>
    Extra,
    /// <summary>Trigger detected, but its sample did not start on the piece's routed output, or it (also) started on
    /// another output.</summary>
    WrongOutput,
    /// <summary>Documented limitation (e.g. a hit below the detection threshold that is expected to be missed).
    /// Shown in the report, does not fail the run.</summary>
    KnownLimitation,
}

/// <summary>A detection reported by the engine for one piece (piece-independent copy of KitEvent/TriggerEvent).</summary>
public readonly record struct DetectedHit(int Piece, long SampleIndex, long PlaybackSampleIndex, float Peak, float Velocity,
    float Gain, bool IsRetrigger);

/// <summary>Polyphony of a piece, to rebuild the expected returns exactly like the voice pool.</summary>
public readonly record struct VoiceLimit(int MaxVoices, int StealFadeSamples);

/// <summary>One row of the harness report.</summary>
public sealed record HarnessRow(
    int Piece, int? InputDesk, int? OutputDesk,
    long? ExpectedSample, long? DetectedSample, long? ErrorSamples, double? ErrorMs,
    float? Velocity, HarnessStatus Status, bool ExpectTrigger, string Label, int? DetectedOutputDesk);

/// <summary>Per-piece totals.</summary>
public sealed record PieceSummary(int Piece, int? InputDesk, int? OutputDesk, int ExpectedTriggers, int ExpectedSilent,
    int Detected, int Ok, int Missing, int Extra, int WrongOutput, double MeanAbsErrorMs, double MaxAbsErrorMs)
{
    /// <summary>Rows with <see cref="HarnessStatus.KnownLimitation"/> (reported, not a failure).</summary>
    public int KnownLimitations { get; init; }

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
        HarnessStatus.KnownLimitation => "limitação conhecida",
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
        sb.AppendLine("| Peça | Entrada | Saída | Esperados | Silêncios esperados | Detectados | ok | faltando | extra | saída-errada | limitação conhecida | erro médio (ms) | erro máx (ms) | Resultado |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var s in Summaries)
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11:0.00} | {12:0.00} | {13} |",
                SyntheticKit.PieceNames[s.Piece], Desk(s.InputDesk), Desk(s.OutputDesk), s.ExpectedTriggers, s.ExpectedSilent,
                s.Detected, s.Ok, s.Missing, s.Extra, s.WrongOutput, s.KnownLimitations, s.MeanAbsErrorMs, s.MaxAbsErrorMs,
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
        var known = Rows.Where(r => r.Status == HarnessStatus.KnownLimitation).ToList();
        if (known.Count > 0)
        {
            sb.AppendLine("## Limitações conhecidas (não reprovam)").AppendLine();
            foreach (var r in known)
                sb.AppendLine($"- {SyntheticKit.PieceNames[r.Piece]}, amostra {r.ExpectedSample?.ToString(Inv) ?? "—"}: {r.Label}");
            sb.AppendLine();
        }
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
/// <para>
/// Triggers: matched per piece, greedy in time order, within ±tolerance.
/// </para>
/// <para>
/// Outputs (attribution by reconstruction, not by "some energy is there"): every detected trigger (and every manual
/// test) is a voice = gain × the piece's sample starting at its playback index, with the voice pool's polyphony and
/// steal fade. The expected signal of each USB output is the sum of the voices of the piece(s) routed to it.
/// For each trigger, on every output, the audio minus all OTHER expected voices is correlated with this voice over the
/// first 3 ms: on the piece's own output the ratio must be ≈ 1 (a NEW onset of this sample, so a ringing earlier
/// voice cannot satisfy it); on any other output it must be ≈ 0. Finally the residual (audio − expected) of each output
/// is scanned for onsets: audio that starts without a trigger explaining it is reported as <see cref="HarnessStatus.Extra"/>.
/// </para>
/// </summary>
public static class ScenarioEvaluator
{
    public const double DefaultToleranceMs = 5.0;
    private const double OwnMinRatio = 0.7, OwnMaxRatio = 1.3, ForeignRatio = 0.5;
    private const float ResidualFloor = 1e-3f;   // -60 dBFS
    private const float OnsetRise = 2.8f;        // +9 dB over the previous 3 windows

    /// <param name="scenario">Scenario with the mic tracks and expectations.</param>
    /// <param name="detections">Automatic detections of every piece.</param>
    /// <param name="physicalOutputs">Routed return audio keyed by desk output channel (USB n).</param>
    /// <param name="inputDesk">Per piece: desk input channel (null = pending).</param>
    /// <param name="outputDesk">Per piece: desk output channel (null = pending).</param>
    /// <param name="toleranceMs">Matching tolerance (ms).</param>
    /// <param name="manualTests">Manual-test voices (not matched against expectations, but they produce audio).</param>
    /// <param name="samples">Sample played by each piece (default: <see cref="KitScenario.Samples"/>).</param>
    /// <param name="voiceLimits">Polyphony per piece (default: 4 voices, 3 ms steal fade).</param>
    public static HarnessReport Evaluate(KitScenario scenario, IReadOnlyList<DetectedHit> detections,
        IReadOnlyDictionary<int, float[]> physicalOutputs, IReadOnlyList<int?> inputDesk, IReadOnlyList<int?> outputDesk,
        double toleranceMs = DefaultToleranceMs, IReadOnlyList<DetectedHit>? manualTests = null,
        IReadOnlyList<float[]?>? samples = null, IReadOnlyList<VoiceLimit>? voiceLimits = null)
    {
        int fs = scenario.SampleRate;
        long tol = (long)Math.Round(toleranceMs * 0.001 * fs);
        bool hasExpectations = scenario.Expected.Count > 0;
        var rows = new List<HarnessRow>();
        var summaries = new List<PieceSummary>();

        var sortedDetections = detections.OrderBy(d => d.SampleIndex).ThenBy(d => d.Piece).ToList();
        var output = new OutputAnalysis(fs, sortedDetections, manualTests ?? [], physicalOutputs, outputDesk,
            samples ?? scenario.Samples, voiceLimits);

        for (int p = 0; p < SyntheticKit.PieceCount; p++)
        {
            var det = sortedDetections.Where(d => d.Piece == p).ToList();
            var expTrig = scenario.Expected.Where(e => e.Piece == p && e.ExpectTrigger).OrderBy(e => e.SampleIndex).ToList();
            var expSilent = scenario.Expected.Where(e => e.Piece == p && !e.ExpectTrigger).OrderBy(e => e.SampleIndex).ToList();
            var matches = MatchWithTolerances(expTrig, det, tol, fs);
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
                var (ok, seenOn, why) = output.Check(d);
                pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, d.SampleIndex, m.ErrorSamples,
                    m.ErrorSamples * 1000.0 / fs, d.Velocity, ok ? HarnessStatus.Ok : HarnessStatus.WrongOutput, true,
                    ok ? e.Label : $"{e.Label} — {why}", seenOn));
            }

            // Must-not-trigger expectations: a detection near one of them is an Extra with that explanation.
            foreach (var e in expSilent)
            {
                int hit = -1;
                for (int i = 0; i < det.Count; i++)
                    if (!usedDetections.Contains(i) && Math.Abs(det[i].SampleIndex - e.SampleIndex) <= tol * 4) { hit = i; break; }
                if (hit < 0)
                {
                    var status = e.Kind == ExpectationKind.BelowThreshold ? HarnessStatus.KnownLimitation : HarnessStatus.Ok;
                    pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], e.SampleIndex, null, null, null, null,
                        status, false, e.Label, null));
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
                var (ok, seenOn, why) = output.Check(d);
                var status = hasExpectations ? HarnessStatus.Extra : ok ? HarnessStatus.Ok : HarnessStatus.WrongOutput;
                string label = hasExpectations ? "disparo inesperado" : (d.IsRetrigger ? "detecção (redisparo)" : "detecção");
                pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], null, d.SampleIndex, null, null, d.Velocity,
                    status, false, ok ? label : $"{label} — {why}", seenOn));
            }

            // Audio that starts on this piece's output without any trigger explaining it.
            if (outputDesk[p] is int myOut)
                foreach (var at in output.UnexplainedOnsets(myOut))
                    pieceRows.Add(new HarnessRow(p, inputDesk[p], outputDesk[p], null, at, null, null, null,
                        HarnessStatus.Extra, false, $"áudio começando na saída USB {myOut} sem disparo que o explique", myOut));

            pieceRows.Sort(RowOrder);
            rows.AddRange(pieceRows);

            var errs = pieceRows.Where(r => r.ExpectTrigger && r.ErrorMs is not null).Select(r => Math.Abs(r.ErrorMs!.Value)).ToList();
            summaries.Add(new PieceSummary(p, inputDesk[p], outputDesk[p], expTrig.Count, expSilent.Count, det.Count,
                pieceRows.Count(r => r.Status == HarnessStatus.Ok),
                pieceRows.Count(r => r.Status == HarnessStatus.Missing),
                pieceRows.Count(r => r.Status == HarnessStatus.Extra),
                pieceRows.Count(r => r.Status == HarnessStatus.WrongOutput),
                errs.Count > 0 ? errs.Average() : 0, errs.Count > 0 ? errs.Max() : 0)
            {
                KnownLimitations = pieceRows.Count(r => r.Status == HarnessStatus.KnownLimitation),
            });
        }
        rows.Sort(RowOrder);
        return new HarnessReport(rows, summaries, fs, hasExpectations);
    }

    /// <summary>
    /// Greedy in-order matching like <see cref="OnsetMatcher.Match"/>, but each expectation may carry its own
    /// tolerance (<see cref="ExpectedEvent.ToleranceMs"/>).
    /// </summary>
    private static List<OnsetMatch> MatchWithTolerances(List<ExpectedEvent> expected, List<DetectedHit> detected, long tol, int fs)
    {
        var result = new List<OnsetMatch>();
        var used = new bool[detected.Count];
        for (int e = 0; e < expected.Count; e++)
        {
            long t = expected[e].ToleranceMs is double ms ? (long)Math.Round(ms * 0.001 * fs) : tol;
            int found = -1;
            for (int d = 0; d < detected.Count; d++)
            {
                if (used[d]) continue;
                long diff = detected[d].SampleIndex - expected[e].SampleIndex;
                if (diff < -t) continue;
                if (diff > t) break;
                found = d;
                break;
            }
            if (found >= 0) { used[found] = true; result.Add(new OnsetMatch(e, expected[e].SampleIndex, found, detected[found].SampleIndex)); }
            else result.Add(new OnsetMatch(e, expected[e].SampleIndex, -1, -1));
        }
        return result;
    }

    private static int RowOrder(HarnessRow a, HarnessRow b)
    {
        int c = (a.ExpectedSample ?? a.DetectedSample ?? 0).CompareTo(b.ExpectedSample ?? b.DetectedSample ?? 0);
        return c != 0 ? c : a.Piece.CompareTo(b.Piece);
    }

    /// <summary>Expected-signal reconstruction and per-voice attribution of the routed outputs.</summary>
    private sealed class OutputAnalysis
    {
        private readonly int _win;
        private readonly IReadOnlyDictionary<int, float[]> _outputs;
        private readonly IReadOnlyList<int?> _outDesk;
        private readonly Dictionary<int, float[]> _expected = new();
        private readonly Dictionary<DetectedHit, Voice> _voiceOf = new();
        private readonly Dictionary<DetectedHit, (bool Ok, int? SeenOn, string Why)> _checks = new();
        private readonly List<(int Channel, long At)> _foreignOnsets = new();

        private sealed class Voice
        {
            public required float[] Data;
            public required long Start;
            public required float Gain;
            public long FadeStart = long.MaxValue;
            public int FadeLength;
            public long End => Math.Min(Start + Data.Length, FadeStart == long.MaxValue ? long.MaxValue : FadeStart + FadeLength);

            /// <summary>Same formula as VoicePool.Render (linear fade from the steal point).</summary>
            public float At(long i)
            {
                long k = i - Start;
                if (k < 0 || k >= Data.Length || i >= End) return 0f;
                float g = Gain;
                if (i >= FadeStart) g = Gain * (FadeLength - (i - FadeStart) - 1) / FadeLength;
                return Data[k] * g;
            }
        }

        public OutputAnalysis(int fs, IReadOnlyList<DetectedHit> detections, IReadOnlyList<DetectedHit> manualTests,
            IReadOnlyDictionary<int, float[]> outputs, IReadOnlyList<int?> outDesk, IReadOnlyList<float[]?> samples,
            IReadOnlyList<VoiceLimit>? limits)
        {
            _win = Math.Max(16, (int)(0.003 * fs));
            _outputs = outputs;
            _outDesk = outDesk;
            int len = outputs.Count == 0 ? 0 : outputs.Values.Max(o => o.Length);

            // Rebuild the voices of each piece in start order, with the voice pool's polyphony/steal rule.
            var all = detections.Concat(manualTests).OrderBy(h => h.PlaybackSampleIndex).ThenBy(h => h.Piece).ToList();
            var active = new List<Voice>[SyntheticKit.PieceCount];
            for (int p = 0; p < active.Length; p++) active[p] = [];
            foreach (var h in all)
            {
                if (h.Piece < 0 || h.Piece >= samples.Count || samples[h.Piece] is not { Length: > 0 } data) continue;
                var lim = limits is not null && h.Piece < limits.Count ? limits[h.Piece] : new VoiceLimit(4, (int)(0.003 * fs));
                var list = active[h.Piece];
                list.RemoveAll(v => v.End <= h.PlaybackSampleIndex);
                var playing = list.Where(v => v.FadeStart == long.MaxValue).OrderBy(v => v.Start).ToList();
                for (int i = 0; playing.Count - i >= lim.MaxVoices && i < playing.Count; i++)
                {
                    playing[i].FadeStart = h.PlaybackSampleIndex;
                    playing[i].FadeLength = Math.Max(1, lim.StealFadeSamples);
                    if (lim.StealFadeSamples <= 0) playing[i].FadeLength = 1;
                }
                var voice = new Voice { Data = data, Start = h.PlaybackSampleIndex, Gain = h.Gain };
                list.Add(voice);
                _voiceOf[h] = voice;
            }

            foreach (var (ch, _) in outputs) _expected[ch] = new float[len];
            foreach (var (h, v) in _voiceOf)
            {
                if (outDesk[h.Piece] is not int ch || !_expected.TryGetValue(ch, out var exp)) continue;
                long end = Math.Min(v.End, len);
                for (long i = Math.Max(0, v.Start); i < end; i++) exp[i] += v.At(i);
            }

            foreach (var h in detections) _checks[h] = Attribute(h);
        }

        public (bool Ok, int? SeenOn, string Why) Check(DetectedHit h)
            => _checks.TryGetValue(h, out var r) ? r : (true, _outDesk[h.Piece], "");

        /// <summary>Correlation ratio of voice <paramref name="v"/> with (output − all other expected voices) on a channel.</summary>
        private double Ratio(Voice v, int channel, bool isOwnChannel)
        {
            var o = _outputs[channel];
            var exp = _expected[channel];
            double num = 0, den = 0;
            for (long i = v.Start; i < v.Start + _win && i < o.Length; i++)
            {
                if (i < 0) continue;
                float vi = v.At(i);
                float others = exp[i] - (isOwnChannel ? vi : 0f);
                num += (o[i] - others) * vi;
                den += (double)vi * vi;
            }
            return den <= 1e-12 ? double.NaN : num / den;
        }

        private (bool Ok, int? SeenOn, string Why) Attribute(DetectedHit h)
        {
            if (!_voiceOf.TryGetValue(h, out var v)) return (true, _outDesk[h.Piece], "");
            int? own = _outDesk[h.Piece];
            double rOwn = own is int oc && _outputs.ContainsKey(oc) ? Ratio(v, oc, true) : 0;
            if (double.IsNaN(rOwn)) return (true, own, "");   // silent start of the sample: nothing to attribute
            var foreign = _outputs.Keys.Where(c => c != own).Select(c => (c, r: Ratio(v, c, false)))
                .Where(x => !double.IsNaN(x.r) && x.r >= ForeignRatio).ToList();
            foreach (var (c, _) in foreign) _foreignOnsets.Add((c, v.Start));
            bool ownOk = rOwn >= OwnMinRatio && rOwn <= OwnMaxRatio;
            if (ownOk && foreign.Count == 0) return (true, own, "");
            string why = !ownOk
                ? $"o sample não começou na saída {(own is null ? "(pendente)" : $"USB {own}")} (correlação {rOwn:0.00})"
                : "o sample começou na saída certa";
            if (foreign.Count > 0) why += $"; também começou em {string.Join(", ", foreign.Select(f => $"USB {f.c}"))}";
            return (false, foreign.Count > 0 ? foreign[0].c : null, why);
        }

        /// <summary>Onsets in (output − expected) not explained by any trigger, as sample indices.</summary>
        public IEnumerable<long> UnexplainedOnsets(int channel)
        {
            if (!_outputs.TryGetValue(channel, out var o)) yield break;
            var exp = _expected[channel];
            int w = _win / 3;                 // 1 ms windows
            int nWin = o.Length / w;
            var peaks = new float[nWin];
            for (int k = 0; k < nWin; k++)
            {
                float m = 0f;
                for (int i = k * w; i < (k + 1) * w; i++) m = Math.Max(m, Math.Abs(o[i] - exp[i]));
                peaks[k] = m;
            }
            // Starts of voices whose attribution already failed on this channel are reported on their own rows.
            var reported = _checks.Where(kv => !kv.Value.Ok && _outDesk[kv.Key.Piece] == channel)
                .Select(kv => kv.Key.PlaybackSampleIndex)
                .Concat(_foreignOnsets.Where(f => f.Channel == channel).Select(f => f.At)).ToList();
            long lastReported = long.MinValue / 2; // no overflow in "at - lastReported"
            for (int k = 0; k < nWin; k++)
            {
                float prev = 0f;
                for (int j = Math.Max(0, k - 3); j < k; j++) prev = Math.Max(prev, peaks[j]);
                if (peaks[k] <= ResidualFloor || peaks[k] <= OnsetRise * prev) continue;
                long at = (long)k * w;
                if (at - lastReported < 20L * w) continue;    // one row per burst (20 ms)
                lastReported = at;
                if (reported.Any(r => Math.Abs(r - at) <= 2L * _win)) continue;
                yield return at;
            }
        }
    }
}
