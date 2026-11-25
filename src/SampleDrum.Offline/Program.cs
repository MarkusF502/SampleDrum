using System.Globalization;
using System.Text;
using SampleDrum.Core;

namespace SampleDrum.Offline;

/// <summary>
/// Modo offline do SampleDrum: processa um WAV de microfone em blocos pelo mesmo TriggerEngine usado ao vivo.
/// </summary>
public static class Program
{
    private const int EngineRate = 48000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var opt = Options.Parse(args);
            if (opt.Help) { PrintUsage(); return 0; }
            if (opt.GenerateDir != null) return GenerateSynthetic(opt.GenerateDir);
            if (opt.Mic == null) { PrintUsage(); return 1; }
            return Run(opt);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Erro nos argumentos: {ex.Message}");
            Console.Error.WriteLine("Use --help para ver as opções.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Erro: {ex.Message}");
            return 1;
        }
    }

    private static int Run(Options o)
    {
        var mic = WavFile.ReadMonoAtRate(o.Mic!, EngineRate, out var micNote);
        if (micNote != null) Console.WriteLine($"Aviso: {micNote}");

        float[]? sample = null;
        if (o.Sample != null)
        {
            sample = WavFile.ReadMonoAtRate(o.Sample, EngineRate, out var sNote);
            if (sNote != null) Console.WriteLine($"Aviso: {sNote}");
        }
        else if (o.Out != null)
        {
            Console.WriteLine("Aviso: --sample não informado; o WAV de saída será silêncio.");
        }

        var s = o.BuildSettings();
        Console.WriteLine($"Microfone: {o.Mic} ({mic.Length} amostras, {mic.Length * 1000.0 / EngineRate:F1} ms @ {EngineRate} Hz)");
        Console.WriteLine($"Bloco: {o.Block} amostras ({o.Block * 1000.0 / EngineRate:F2} ms)");
        Console.WriteLine(string.Format(Inv,
            "Ajustes: limiar {0} dBFS, detect time {1} ms, rearme {2} dBFS ({3:+0.#;-0.#;0} dB) por {4} ms, intervalo mínimo {5} ms, " +
            "redisparo {6}, janela de varredura {7} ms, release do envelope {8} ms, passa-altas {9}, passa-baixas {10}, " +
            "ganho mínimo {11} dB{12}",
            s.ThresholdDb, s.DetectTimeMs, s.RearmDb, s.ReleaseRelativeDb, s.RearmTimeMs, s.MinIntervalMs,
            s.RetriggerRiseDb > 0 ? $"+{s.RetriggerRiseDb.ToString(Inv)} dB" : "desligado", s.ScanMs, s.EnvelopeReleaseMs,
            s.HighPassHz > 0 ? $"{s.HighPassHz.ToString(Inv)} Hz" : "desligado",
            s.LowPassHz > 0 ? $"{s.LowPassHz.ToString(Inv)} Hz" : "desligado",
            s.MinGainDb, s.FixedVelocity ? " (velocity fixa)" : ""));

        var result = OfflineProcessor.Run(mic, sample, EngineRate, s, o.Block);

        Console.WriteLine();
        Console.WriteLine($"Eventos detectados: {result.Events.Count}");
        int i = 1;
        foreach (var e in result.Events)
        {
            Console.WriteLine(string.Format(Inv,
                "  #{0,-3} t={1,10:F3} ms  amostra={2,-9}  pico={3,6:F1} dBFS  vel={4:F2}  ganho={5,6:F1} dB  início do sample={6}{7}",
                i++, e.SampleIndex * 1000.0 / EngineRate, e.SampleIndex, Db.FromLinear(e.Peak), e.Velocity,
                Db.FromLinear(e.Gain), e.PlaybackSampleIndex, e.IsRetrigger ? "  (redisparo no sustain)" : ""));
        }
        if (result.DroppedEvents > 0) Console.WriteLine($"Aviso: {result.DroppedEvents} eventos descartados (fila cheia).");

        if (o.Out != null)
        {
            WavFile.WriteMono(o.Out, result.Output, EngineRate, o.Format);
            Console.WriteLine($"WAV de saída (só o trigger): {o.Out} ({o.Format}, {EngineRate} Hz, mono)");
        }

        if (o.Expected != null)
            return Compare(o.Expected, result.Events, o.ToleranceMs);
        return 0;
    }

    private static int Compare(string path, IReadOnlyList<TriggerEvent> events, double toleranceMs)
    {
        var expected = ReadExpected(path);
        var detected = events.Select(e => e.SampleIndex).ToList();
        long tol = (long)Math.Round(toleranceMs * EngineRate / 1000.0);
        var matches = OnsetMatcher.Match(expected, detected, tol);

        Console.WriteLine();
        Console.WriteLine(string.Format(Inv, "Comparação com {0} (tolerância ±{1} ms = ±{2} amostras):", path, toleranceMs, tol));
        int ok = 0, miss = 0, fp = 0;
        double maxAbsErr = 0;
        foreach (var m in matches.OrderBy(m => m.ExpectedSample >= 0 ? m.ExpectedSample : m.DetectedSample))
        {
            if (m.IsMatch)
            {
                ok++;
                double errMs = m.ErrorSamples * 1000.0 / EngineRate;
                maxAbsErr = Math.Max(maxAbsErr, Math.Abs(errMs));
                Console.WriteLine(string.Format(Inv, "  OK      esperado={0,-9} detectado={1,-9} erro={2,5:+0;-0;0} amostras ({3,8:+0.000;-0.000;0.000} ms)",
                    m.ExpectedSample, m.DetectedSample, m.ErrorSamples, errMs));
            }
            else if (m.IsMiss)
            {
                miss++;
                Console.WriteLine(string.Format(Inv, "  PERDIDO esperado={0,-9} ({1:F3} ms) não detectado", m.ExpectedSample, m.ExpectedSample * 1000.0 / EngineRate));
            }
            else
            {
                fp++;
                Console.WriteLine(string.Format(Inv, "  EXTRA   detectado={0,-9} ({1:F3} ms) sem correspondente esperado", m.DetectedSample, m.DetectedSample * 1000.0 / EngineRate));
            }
        }
        Console.WriteLine(string.Format(Inv, "Resumo: {0} corretos, {1} perdidos, {2} extras; maior erro absoluto {3:F3} ms.", ok, miss, fp, maxAbsErr));
        return miss == 0 && fp == 0 ? 0 : 2;
    }

    /// <summary>
    /// Reads expected onsets from a CSV with a header containing <c>sample_index</c> or <c>time_ms</c>
    /// (lines starting with # are ignored), or from a plain list of sample indices (one per line).
    /// </summary>
    internal static List<long> ReadExpected(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        var result = new List<long>();
        if (lines.Count == 0) return result;
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int idxCol = header.IndexOf("sample_index");
        int msCol = header.IndexOf("time_ms");
        bool hasHeader = idxCol >= 0 || msCol >= 0;
        foreach (var line in hasHeader ? lines.Skip(1) : lines)
        {
            var cols = line.Split(',');
            if (idxCol >= 0) result.Add(long.Parse(cols[idxCol].Trim(), Inv));
            else if (msCol >= 0) result.Add((long)Math.Round(double.Parse(cols[msCol].Trim(), Inv) * EngineRate / 1000.0));
            else result.Add(long.Parse(cols[0].Trim(), Inv));
        }
        result.Sort();
        return result;
    }

    private static int GenerateSynthetic(string dir)
    {
        Directory.CreateDirectory(dir);
        var (mic, hits) = SyntheticSignals.MicTrack(EngineRate);
        var kick = SyntheticSignals.KickSample(EngineRate);
        string micPath = Path.Combine(dir, "mic.wav");
        string kickPath = Path.Combine(dir, "kick.wav");
        string csvPath = Path.Combine(dir, "expected.csv");
        WavFile.WriteMono(micPath, mic, EngineRate, WavSampleFormat.Pcm24);
        WavFile.WriteMono(kickPath, kick, EngineRate, WavSampleFormat.Pcm24);

        var sb = new StringBuilder();
        sb.AppendLine("# Onsets esperados do mic.wav sintético (48 kHz). sample_index = início do golpe.");
        sb.AppendLine("sample_index,time_ms,level_db,sustain_ms,label");
        foreach (var h in hits.OrderBy(h => h.StartSample))
            sb.AppendLine(string.Format(Inv, "{0},{1:F3},{2},{3},{4}", h.StartSample, h.StartSample * 1000.0 / EngineRate, h.LevelDb, h.SustainMs, h.Label));
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));

        Console.WriteLine($"Gerado: {micPath} ({mic.Length} amostras, PCM24 48 kHz mono, {hits.Count} golpes)");
        Console.WriteLine($"Gerado: {kickPath} ({kick.Length} amostras, PCM24 48 kHz mono)");
        Console.WriteLine($"Gerado: {csvPath}");
        Console.WriteLine();
        Console.WriteLine("Exemplo:");
        Console.WriteLine($"  dotnet run --project src/SampleDrum.Offline -- --mic {micPath} --sample {kickPath} --out {Path.Combine(dir, "triggers.wav")} --expected {csvPath}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SampleDrum.Offline — processa um WAV de microfone de bumbo pelo mesmo motor de trigger usado ao vivo.

            Uso:
              SampleDrum.Offline --mic mic.wav [--sample kick.wav] [--out triggers.wav] [opções]
              SampleDrum.Offline --generate-synthetic <pasta>

            Opções:
              --block N              tamanho do bloco em amostras (padrão 128)
              --threshold-db X       limiar de disparo (detect) em dBFS (padrão -24)
              --detect-ms X          debounce após o cruzamento; é latência (padrão 0.5; 0 = desligado)
              --rearm-db X           nível de release/rearme em dBFS, <= limiar (padrão: limiar - 6)
              --rearm-ms X           tempo abaixo do release antes de rearmar (padrão 15)
              --min-interval-ms X    trava absoluta entre disparos (padrão 35)
              --retrigger-db X       redisparo no sustain se o envelope subir X dB; 0 = desligado (padrão 9)
              --scan-ms X            janela extra de busca do pico/velocity (padrão 0 = só o detect time)
              --release-ms X         release do envelope peak-hold (padrão 20)
              --hpf-hz X             passa-altas na detecção; 0 = desligado (padrão 40)
              --lpf-hz X             passa-baixas na detecção; 0 = desligado (padrão 0)
              --min-gain-db X        ganho do sample com velocity 0 (padrão -20; velocity 1 = 0 dB)
              --fixed-velocity       todo golpe toca a 0 dB
              --format float|pcm24|pcm16   formato do WAV de saída (padrão float)
              --expected arquivo.csv compara com os onsets esperados (coluna sample_index ou time_ms)
              --tolerance-ms X       tolerância da comparação (padrão 2)

            Código de saída: 0 = ok; 1 = erro de argumentos/arquivo; 2 = comparação com --expected falhou.
            """);
    }

    private sealed class Options
    {
        public bool Help;
        public string? Mic, Sample, Out, Expected, GenerateDir;
        public int Block = 128;
        public float? ThresholdDb, DetectMs, RearmDb, RearmMs, MinIntervalMs, RetriggerDb, ScanMs, ReleaseMs, HpfHz, LpfHz, MinGainDb;
        public bool FixedVelocity;
        public double ToleranceMs = 2.0;
        public WavSampleFormat Format = WavSampleFormat.Float32;

        public TriggerSettings BuildSettings()
        {
            var d = TriggerSettings.Default;
            float thr = ThresholdDb ?? d.ThresholdDb;
            var s = d.WithThreshold(thr) with
            {
                DetectTimeMs = DetectMs ?? d.DetectTimeMs,
                RearmTimeMs = RearmMs ?? d.RearmTimeMs,
                MinIntervalMs = MinIntervalMs ?? d.MinIntervalMs,
                RetriggerRiseDb = RetriggerDb ?? d.RetriggerRiseDb,
                ScanMs = ScanMs ?? d.ScanMs,
                EnvelopeReleaseMs = ReleaseMs ?? d.EnvelopeReleaseMs,
                HighPassHz = HpfHz ?? d.HighPassHz,
                LowPassHz = LpfHz ?? d.LowPassHz,
                MinGainDb = MinGainDb ?? d.MinGainDb,
                FixedVelocity = FixedVelocity,
            };
            if (RearmDb is { } rearm) s = s with { RearmDb = rearm };
            if (RearmDb is { } r && r > thr)
                Console.WriteLine($"Aviso: --rearm-db ({r}) acima do limiar ({thr}); usando o limiar.");
            return s.Normalized();
        }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} precisa de um valor.");
                float F() { var v = Next(); return float.TryParse(v, NumberStyles.Float, Inv, out var f) ? f : throw new ArgumentException($"{a}: número inválido '{v}' (use ponto decimal)."); }
                switch (a)
                {
                    case "-h": case "--help": o.Help = true; break;
                    case "--mic": o.Mic = Next(); break;
                    case "--sample": o.Sample = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--expected": o.Expected = Next(); break;
                    case "--generate-synthetic": o.GenerateDir = Next(); break;
                    case "--block":
                        var b = Next();
                        if (!int.TryParse(b, NumberStyles.Integer, Inv, out o.Block) || o.Block < 1 || o.Block > 1 << 16)
                            throw new ArgumentException($"--block inválido '{b}' (1..65536).");
                        break;
                    case "--threshold-db": o.ThresholdDb = F(); break;
                    case "--rearm-db": o.RearmDb = F(); break;
                    case "--rearm-ms": o.RearmMs = F(); break;
                    case "--min-interval-ms": o.MinIntervalMs = F(); break;
                    case "--scan-ms": o.ScanMs = F(); break;
                    case "--release-ms": o.ReleaseMs = F(); break;
                    case "--hpf-hz": o.HpfHz = F(); break;
                    case "--detect-ms": o.DetectMs = F(); break;
                    case "--retrigger-db": o.RetriggerDb = F(); break;
                    case "--lpf-hz": o.LpfHz = F(); break;
                    case "--min-gain-db": o.MinGainDb = F(); break;
                    case "--fixed-velocity": o.FixedVelocity = true; break;
                    case "--tolerance-ms": o.ToleranceMs = F(); break;
                    case "--format":
                        o.Format = Next().ToLowerInvariant() switch
                        {
                            "float" or "float32" => WavSampleFormat.Float32,
                            "pcm24" => WavSampleFormat.Pcm24,
                            "pcm16" => WavSampleFormat.Pcm16,
                            var f => throw new ArgumentException($"--format inválido '{f}' (float, pcm24, pcm16)."),
                        };
                        break;
                    default: throw new ArgumentException($"opção desconhecida '{a}'.");
                }
            }
            return o;
        }
    }
}
