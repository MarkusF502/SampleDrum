using System.Globalization;
using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>Options of a harness run.</summary>
public sealed record HarnessOptions
{
    /// <summary>Block size in frames (same options as ASIO: 32/64/128/256/480...).</summary>
    public int BlockSize { get; init; } = 256;

    /// <summary>Matching tolerance (ms) between expected and detected onsets.</summary>
    public double ToleranceMs { get; init; } = ScenarioEvaluator.DefaultToleranceMs;

    /// <summary>Routing used to map pieces to USB inputs/outputs (default: the built-in "Simulação" profile).</summary>
    public RoutingProfile Profile { get; init; } = RoutingProfiles.Simulation;

    /// <summary>Preview mix written to previa-estereo.wav (mics 18 dB under the returns by default).</summary>
    public PreviewMixSettings Preview { get; init; } = new() { MicLevelDb = -18f };
}

/// <summary>Result of a harness run (offline or captured from the real-time simulation host).</summary>
public sealed class HarnessResult
{
    public required KitScenario Scenario { get; init; }
    public required RoutingValidation Validation { get; init; }
    public required ResolvedRouting Routing { get; init; }
    public required HarnessReport Report { get; init; }
    /// <summary>Return of each piece as written to its routed output (silence when not routed), index = DrumPiece.</summary>
    public required float[][] ReturnsByPiece { get; init; }
    /// <summary>Routed return audio per desk output channel (USB n → samples).</summary>
    public required SortedDictionary<int, float[]> PhysicalOutputs { get; init; }
    public required IReadOnlyList<KitEvent> Events { get; init; }
    public required int BlockSize { get; init; }
    public required string Mode { get; init; }

    /// <summary>Desk input / output channel per piece (null = not routed).</summary>
    public IReadOnlyList<int?> InputDesk => DrumPieces.All.Select(p => Routing.InputIndex(p) + 1).ToArray();
    public IReadOnlyList<int?> OutputDesk => DrumPieces.All.Select(p => Routing.OutputIndex(p) + 1).ToArray();
}

/// <summary>
/// Reproducible scenario runner: drives the SAME <see cref="KitEngine"/> used live, in fixed blocks, emulating the host
/// routing exactly (each piece reads its routed USB input, silence if unrouted; its return goes to its routed USB output,
/// discarded if unrouted). Writes the return WAVs, a 4-channel return WAV, a stereo preview mix and a CSV/Markdown report.
/// Deterministic: same seed and block size => identical output.
/// </summary>
public static class KitHarness
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Per-piece settings for the scenario: the scenario's recommended detection settings.</summary>
    public static KitSettings SettingsFor(KitScenario sc)
        => new(DrumPieces.All.Select(p => new PieceSettings { Trigger = sc.RecommendedSettings((int)p) }).ToArray());

    /// <summary>The scenario's synthetic samples as a prepared kit.</summary>
    public static PreparedKit KitFor(KitScenario sc, string name = "Kit sintético")
        => new(name, sc.SampleRate, DrumPieces.All.Select(p =>
            new PreparedPiece(p, sc.Samples[(int)p], null, $"sintetico-{SyntheticKit.PieceSlugs[(int)p]}")));

    /// <summary>Engine ready for the scenario: settings, kit applied, kit and every piece enabled.</summary>
    public static KitEngine CreateEngine(KitScenario sc)
    {
        var e = new KitEngine(sc.SampleRate, SettingsFor(sc));
        e.ApplyKit(KitFor(sc));
        foreach (var p in DrumPieces.All) e.SetPieceEnabled(p, true);
        e.KitEnabled = true;
        return e;
    }

    public static DetectedHit ToHit(KitEvent e) => new((int)e.Piece, e.Event.SampleIndex, e.Event.PlaybackSampleIndex,
        e.Event.Peak, e.Event.Velocity, e.Event.Gain, e.Event.IsRetrigger);

    /// <summary>Runs the scenario offline through <paramref name="engine"/> (or a fresh one) and evaluates it.</summary>
    public static HarnessResult RunOffline(KitScenario sc, HarnessOptions? options = null, KitEngine? engine = null)
    {
        var o = options ?? new HarnessOptions();
        if (o.BlockSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Bloco deve ser > 0.");
        var validation = RoutingValidator.Validate(o.Profile);
        var routing = validation.Resolve();
        engine ??= CreateEngine(sc);

        int len = sc.Length, block = o.BlockSize, n4 = DrumPieces.Count;
        // The "desk": mic of piece p arrives on the USB input of the same profile (ASIO index).
        var deskInputs = new Dictionary<int, float[]>();
        foreach (var p in DrumPieces.All)
            if (validation.Profile[p].InputDeskChannel is int ch && routing.InputIndex(p) is not null)
                deskInputs[ch - 1] = sc.Mics[(int)p];

        var inputs = new float[n4][];
        var outputs = new float[n4][];
        for (int i = 0; i < n4; i++) { inputs[i] = new float[block]; outputs[i] = new float[block]; }
        var byPiece = new float[n4][];
        for (int i = 0; i < n4; i++) byPiece[i] = new float[len];
        var physical = new SortedDictionary<int, float[]>();
        foreach (var p in DrumPieces.All)
            if (routing.OutputIndex(p) is int oi && !physical.ContainsKey(oi + 1)) physical[oi + 1] = new float[len];

        var events = new List<KitEvent>();
        for (int start = 0; start < len; start += block)
        {
            int n = Math.Min(block, len - start);
            foreach (var p in DrumPieces.All)
            {
                int i = (int)p;
                if (routing.InputIndex(p) is int ii && deskInputs.TryGetValue(ii, out var src))
                    Array.Copy(src, start, inputs[i], 0, n);
                else
                    Array.Clear(inputs[i], 0, n);
                Array.Clear(outputs[i], 0, n);
            }
            engine.Process(inputs, outputs, n);
            foreach (var p in DrumPieces.All)
            {
                if (routing.OutputIndex(p) is not int oi) continue; // not routed: return discarded
                int i = (int)p;
                Array.Copy(outputs[i], 0, byPiece[i], start, n);
                var dst = physical[oi + 1];
                for (int k = 0; k < n; k++) dst[start + k] += outputs[i][k];
            }
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
        }
        return Evaluate(sc, o, validation, routing, byPiece, physical, events, block, "offline (determinístico)");
    }

    /// <summary>Builds the result/report from captured returns and events (also used by the real-time host capture).</summary>
    public static HarnessResult Evaluate(KitScenario sc, HarnessOptions o, RoutingValidation validation, ResolvedRouting routing,
        float[][] byPiece, SortedDictionary<int, float[]> physical, IReadOnlyList<KitEvent> events, int blockSize, string mode)
    {
        var hits = events.Where(e => !e.Event.IsManualTest).Select(ToHit).ToList();
        var inDesk = DrumPieces.All.Select(p => routing.InputIndex(p) + 1).ToArray();
        var outDesk = DrumPieces.All.Select(p => routing.OutputIndex(p) + 1).ToArray();
        var report = ScenarioEvaluator.Evaluate(sc, hits, physical, inDesk, outDesk, o.ToleranceMs);
        return new HarnessResult
        {
            Scenario = sc, Validation = validation, Routing = routing, Report = report, ReturnsByPiece = byPiece,
            PhysicalOutputs = physical, Events = events, BlockSize = blockSize, Mode = mode,
        };
    }

    /// <summary>
    /// Writes the run's files into <paramref name="dir"/> and returns their paths:
    /// mic-*.wav and mics-4ch.wav (the inputs used), retorno-*.wav (one per piece), retornos-4ch.wav
    /// (channel n = USB output n), previa-estereo.wav, relatorio.csv, relatorio.md.
    /// </summary>
    public static IReadOnlyList<string> WriteFiles(HarnessResult r, string dir, HarnessOptions o, IEnumerable<string>? extraHeader = null)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        int fs = r.Scenario.SampleRate;
        string F(string name) { var p = Path.Combine(dir, name); files.Add(p); return p; }

        for (int i = 0; i < SyntheticKit.PieceCount; i++)
            WavFile.WriteMono(F($"mic-{SyntheticKit.PieceSlugs[i]}.wav"), r.Scenario.Mics[i], fs);
        MultiChannelWav.Write(F("mics-4ch.wav"), r.Scenario.Mics, fs);
        for (int i = 0; i < SyntheticKit.PieceCount; i++)
            WavFile.WriteMono(F($"retorno-{SyntheticKit.PieceSlugs[i]}.wav"), r.ReturnsByPiece[i], fs);
        int maxOut = Math.Max(4, r.PhysicalOutputs.Count == 0 ? 0 : r.PhysicalOutputs.Keys.Max());
        var outCh = Enumerable.Range(1, maxOut)
            .Select(ch => r.PhysicalOutputs.TryGetValue(ch, out var b) ? b : new float[r.Scenario.Length]).ToList();
        MultiChannelWav.Write(F("retornos-4ch.wav"), outCh, fs);
        MultiChannelWav.Write(F("previa-estereo.wav"), PreviewMix.Mix(r.ReturnsByPiece, r.Scenario.Mics, o.Preview), fs);

        r.Report.WriteCsv(F("relatorio.csv"));
        var header = new List<string>
        {
            $"Modo: {r.Mode}; bloco {r.BlockSize} amostras ({r.BlockSize * 1000.0 / fs:0.00} ms) @ {fs} Hz",
            r.Scenario.Expected.Count > 0
                ? $"Cenário sintético, semente {r.Scenario.Seed}: {r.Scenario.Hits.Count} batidas, {r.Scenario.Expected.Count} expectativas, duração {r.Scenario.Length / (double)fs:0.0} s"
                : $"Microfones fornecidos pelo usuário ({r.Scenario.Length / (double)fs:0.0} s), sem gabarito",
            $"Roteamento: perfil \"{r.Validation.Profile.Name}\" — " + string.Join("; ", DrumPieces.All.Select(p =>
                $"{p.Label()} entrada {Desk(r.InputDesk[(int)p])} → saída {Desk(r.OutputDesk[(int)p])}")),
            "Limiar por peça: " + string.Join(", ", DrumPieces.All.Select(p =>
                $"{p.Label()} {r.Scenario.ThresholdDb[(int)p].ToString("0.#", Inv)} dBFS")),
            $"Tolerância de posição: ±{o.ToleranceMs.ToString("0.##", Inv)} ms",
            "Arquivos: retorno-<peça>.wav (retorno de cada peça), retornos-4ch.wav (canal n = saída USB n), " +
            "previa-estereo.wav (mistura para fone; microfones 18 dB abaixo), mic-<peça>.wav / mics-4ch.wav (entradas usadas)",
        };
        foreach (var issue in r.Validation.Issues) header.Add($"Roteamento: {issue.Message}");
        if (extraHeader is not null) header.AddRange(extraHeader);
        r.Report.WriteMarkdown(F("relatorio.md"), "SampleDrum — relatório do cenário de teste (4 peças)", header);
        return files;
    }

    private static string Desk(int? ch) => ch is null ? "pendente" : $"USB {ch}";
}
