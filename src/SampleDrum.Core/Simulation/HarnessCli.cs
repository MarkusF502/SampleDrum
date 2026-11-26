using System.Globalization;

namespace SampleDrum.Core.Simulation;

/// <summary>Parsed options of the scenario runner command line.</summary>
public sealed record HarnessCliOptions
{
    public string OutputDir { get; init; } = "";
    /// <summary>Four mic WAVs (Bumbo, Caixa, Tom, Surdo); null = synthetic scenario.</summary>
    public IReadOnlyList<string>? Mics { get; init; }
    /// <summary>One multichannel WAV with the four mics; channels from <see cref="Channels"/>.</summary>
    public string? MultichannelMics { get; init; }
    public IReadOnlyList<int> Channels { get; init; } = [1, 2, 3, 4];
    public int BlockSize { get; init; } = 256;
    public int Seed { get; init; } = 1;
    public double ToleranceMs { get; init; } = ScenarioEvaluator.DefaultToleranceMs;
    /// <summary>Also run through the real-time simulation host (App only).</summary>
    public bool Realtime { get; init; }
    /// <summary>Real-time run with the stereo preview on the PC's default output (App only; implies Realtime).</summary>
    public bool Play { get; init; }

    /// <summary>Per piece path of the mic for a real-time run (null for synthetic / multichannel).</summary>
    public IReadOnlyList<string?>? MicPaths => Mics?.Select(m => (string?)m).ToArray();
}

/// <summary>
/// Shared command line of the scenario runner, used by <c>SampleDrum.Offline --kit</c> (cross-platform) and
/// <c>SampleDrum.App.exe --harness</c> (Windows, adds the real-time host and the audible preview).
/// Exit code: 0 when every expectation passes (or, with the user's own mics, when the run completed), 1 on failure,
/// 2 on argument / file errors.
/// </summary>
public static class HarnessCli
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Real-time run provided by the App: (scenario, options, log) → result.</summary>
    public delegate HarnessResult RealtimeRunner(KitScenario scenario, HarnessCliOptions options, Action<string> log);

    public static string Usage(string command) => $"""
        Uso: {command} <pasta-de-saída> [opções]
          (sem --mics)            gera o cenário sintético de 4 peças (semente --seed) com gabarito
          --mics a b c d          4 WAVs de microfone: Bumbo, Caixa, Tom, Surdo (sem gabarito: só lista as detecções)
          --mics4 arquivo.wav     1 WAV multicanal com os 4 microfones; --channels 1,2,3,4 escolhe os canais
          --block N               tamanho do bloco (64/128/256/480; padrão 256)
          --seed S                semente do cenário sintético (padrão 1)
          --tolerance ms          tolerância de posição (padrão 5 ms)
          --realtime              roda também pelo host de simulação em tempo real (só no SampleDrum.App.exe)
          --play                  idem, tocando a prévia estéreo na saída padrão do PC (fone/caixas)
        Saídas: retorno-bumbo/caixa/tom/surdo.wav, retornos-4ch.wav (canal n = saída USB n), previa-estereo.wav,
                mic-*.wav, mics-4ch.wav, relatorio.csv, relatorio.md (e subpasta tempo-real/ com --realtime/--play).
        Código de saída: 0 = tudo conforme o gabarito; 1 = alguma expectativa falhou; 2 = erro de argumentos/arquivos.
        No PowerShell o SampleDrum.App.exe é um programa de janela: NÃO use "> arquivo" (o PowerShell não espera e o
        arquivo fica vazio). Use "| Out-Host; $LASTEXITCODE" ou "| Out-File saida.txt; Get-Content saida.txt;
        $LASTEXITCODE"; no Prompt de Comando, "start /wait".
        """;

    public static HarnessCliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Informe a pasta de saída como primeiro argumento.");
        var o = new HarnessCliOptions { OutputDir = args[0] };
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{a} precisa de um valor.");
            switch (a)
            {
                case "--mics":
                    if (args.Count - i - 1 < 4) throw new ArgumentException("--mics precisa de 4 arquivos (Bumbo, Caixa, Tom, Surdo).");
                    o = o with { Mics = [args[++i], args[++i], args[++i], args[++i]] };
                    break;
                case "--mics4": o = o with { MultichannelMics = Next() }; break;
                case "--channels":
                    var ch = Next().Split(',').Select(s => Int(a, s.Trim())).ToArray();
                    if (ch.Length != 4) throw new ArgumentException("--channels precisa de 4 números (ex.: 1,2,3,4).");
                    o = o with { Channels = ch };
                    break;
                case "--block": o = o with { BlockSize = Int(a, Next()) }; break;
                case "--seed": o = o with { Seed = Int(a, Next()) }; break;
                case "--tolerance":
                    string t = Next();
                    o = o with { ToleranceMs = double.TryParse(t, NumberStyles.Float, Inv, out var tol) && tol > 0 ? tol
                        : throw new ArgumentException($"{a}: valor inválido \"{t}\" (use ponto decimal, ex.: 2.5).") };
                    break;
                case "--realtime": o = o with { Realtime = true }; break;
                case "--play": o = o with { Play = true, Realtime = true }; break;
                default: throw new ArgumentException($"Opção desconhecida: {a}");
            }
        }
        if (o.BlockSize <= 0 || o.BlockSize > 8192) throw new ArgumentException("--block deve estar entre 1 e 8192.");
        if (o.Mics is not null && o.MultichannelMics is not null) throw new ArgumentException("Use --mics ou --mics4, não os dois.");
        return o;
    }

    private static int Int(string option, string value) =>
        int.TryParse(value, NumberStyles.Integer, Inv, out var v) ? v
            : throw new ArgumentException($"{option}: valor inválido \"{value}\" (esperado um número inteiro).");

    /// <summary>Builds the scenario: synthetic (seeded) or from the user's mics (resampled to 48 kHz).</summary>
    public static KitScenario LoadScenario(HarnessCliOptions o, Action<string> log)
    {
        const int rate = KitScenario.DefaultSampleRate;
        if (o.Mics is null && o.MultichannelMics is null) return KitScenario.Generate(o.Seed, rate);
        var mics = new float[4][];
        for (int p = 0; p < 4; p++)
        {
            string path = o.Mics?[p] ?? o.MultichannelMics!;
            int channel = o.Mics is not null ? -1 : o.Channels[p] - 1;
            mics[p] = WavFile.ReadMonoAtRate(path, rate, channel, out var note);
            if (note is not null) log($"{SyntheticKit.PieceNames[p]}: {note}");
        }
        return KitScenario.FromMics(mics, rate);
    }

    public static int Run(IReadOnlyList<string> args, string command, RealtimeRunner? realtime = null, TextWriter? output = null)
    {
        var w = output ?? Console.Out;
        void Log(string s) => w.WriteLine(s);
        HarnessCliOptions o;
        KitScenario sc;
        try
        {
            if (args.Count > 0 && args[0] is "--help" or "-h") { Log(Usage(command)); return 0; }
            o = Parse(args);
            sc = LoadScenario(o, Log);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidDataException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            Log($"Erro: {(ex is IOException or UnauthorizedAccessException ? UserMessages.FileError(ex) : ex.Message)}");
            Log(Usage(command));
            return 2;
        }

        var ho = new HarnessOptions { BlockSize = o.BlockSize, ToleranceMs = o.ToleranceMs };
        Log(sc.Expected.Count > 0
            ? $"Cenário sintético: semente {sc.Seed}, {sc.Hits.Count} batidas, {sc.Expected.Count} expectativas, {sc.Length / (double)sc.SampleRate:0.0} s @ {sc.SampleRate} Hz"
            : $"Microfones do usuário: {sc.Length / (double)sc.SampleRate:0.0} s @ {sc.SampleRate} Hz (sem gabarito)");
        Log($"Bloco: {o.BlockSize} amostras ({o.BlockSize * 1000.0 / sc.SampleRate:0.00} ms); perfil de roteamento: {ho.Profile.Name}");

        var off = KitHarness.RunOffline(sc, ho);
        var files = KitHarness.WriteFiles(off, o.OutputDir, ho);
        Log("");
        Log($"== Offline (determinístico) — {(off.Report.HasExpectations ? off.Report.Passed ? "OK" : "FALHOU" : "sem gabarito")}");
        Log(off.Report.SummaryTable());
        foreach (var row in off.Report.Rows.Where(r => off.Report.HasExpectations && r.Status != HarnessStatus.Ok))
            Log($"  {SyntheticKit.PieceNames[row.Piece]}: {HarnessReport.StatusText(row.Status)} — {row.Label} (esperado {row.ExpectedSample?.ToString(Inv) ?? "—"}, detectado {row.DetectedSample?.ToString(Inv) ?? "—"})");
        Log($"Arquivos em {Path.GetFullPath(o.OutputDir)}: {string.Join(", ", files.Select(Path.GetFileName))}");
        bool ok = !off.Report.HasExpectations || off.Report.Passed;

        if (o.Realtime)
        {
            if (realtime is null)
            {
                Log("");
                Log("Aviso: --realtime/--play só existem no SampleDrum.App.exe (Windows); rodada em tempo real ignorada.");
            }
            else
            {
                Log("");
                Log(o.Play ? $"Tocando a prévia em tempo real ({sc.Length / (double)sc.SampleRate:0.0} s) na saída padrão do PC..."
                           : $"Rodando em tempo real ({sc.Length / (double)sc.SampleRate:0.0} s)...");
                try
                {
                    var rt = realtime(sc, o, Log);
                    string rtDir = Path.Combine(o.OutputDir, "tempo-real");
                    var rtFiles = KitHarness.WriteFiles(rt, rtDir, ho);
                    Log($"== {rt.Mode} — {(rt.Report.HasExpectations ? rt.Report.Passed ? "OK" : "FALHOU" : "sem gabarito")}");
                    Log(rt.Report.SummaryTable());
                    Log($"Arquivos em {Path.GetFullPath(rtDir)}");
                    ok &= !rt.Report.HasExpectations || rt.Report.Passed;
                }
                catch (Exception ex)
                {
                    Log($"Erro na rodada em tempo real: {ex.Message}");
                    ok = false;
                }
            }
        }
        Log("");
        Log(ok ? "RESULTADO: OK" : "RESULTADO: FALHOU");
        return ok ? 0 : 1;
    }
}
