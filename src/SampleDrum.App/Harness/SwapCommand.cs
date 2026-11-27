using System.Globalization;
using SampleDrum.App.Audio;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Harness;

/// <summary>
/// Swap verification (<c>SampleDrum.App.exe --harness --swap &lt;pasta&gt;</c>, alias <c>--harness-swap</c>): the
/// offline run of <see cref="SwapHarness"/> plus, with <c>--realtime</c>, the same timeline through the running
/// simulation host (<see cref="FakeAudioHost"/>): the controller operations are issued while the host keeps
/// processing, exactly like the PC UI or the phone would.
/// </summary>
public static class SwapCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Usage => """
        Uso: SampleDrum.App.exe --harness --swap <pasta-de-saída> [--block N] [--seed S] [--realtime] [--speed X]
          Toca o cenário de 4 peças com a biblioteca de demonstração e, no meio do áudio, troca o sample do bumbo,
          aplica um preset inteiro e tenta um preset com um WAV faltando; confere no áudio gravado de cada retorno
          qual sample cada golpe usou.
          --block N     tamanho do bloco (padrão 256)
          --seed S      semente do cenário (padrão 1)
          --realtime    também pelo host de simulação em tempo real (operações enquanto o áudio roda)
          --speed X     ritmo da rodada em tempo real (padrão 1 = tempo real)
        Saídas: relatorio-trocas.md, relatorio-trocas.csv (uma linha por golpe), retornos-4ch.wav (e tempo-real\).
        Código de saída: 0 = todas as verificações OK; 1 = alguma falhou; 2 = erro de argumentos.
        No PowerShell use "| Out-Host; $LASTEXITCODE" (nunca "> arquivo").
        """;

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.WriteLine(Usage);
            return args.Count > 0 && args[0] is "--help" or "-h" ? 0 : 2;
        }
        string outDir = args[0];
        var o = new SwapOptions();
        bool realtime = false;
        double speed = 1.0;
        try
        {
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{a} precisa de um valor.");
                switch (a)
                {
                    case "--block": o = o with { BlockSize = Int(a, Next()) }; break;
                    case "--seed": o = o with { Seed = Int(a, Next()) }; break;
                    case "--realtime": realtime = true; break;
                    case "--speed":
                        string v = Next();
                        speed = double.TryParse(v, NumberStyles.Float, Inv, out var s) && s > 0 ? s
                            : throw new ArgumentException($"--speed: valor inválido \"{v}\".");
                        break;
                    default: throw new ArgumentException($"Opção desconhecida: {a}");
                }
            }
            if (o.BlockSize is <= 0 or > 8192) throw new ArgumentException("--block deve estar entre 1 e 8192.");
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"Erro: {ex.Message}");
            Console.WriteLine(Usage);
            return 2;
        }

        string libRoot = Path.Combine(Path.GetTempPath(), "SampleDrum-trocas-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var lib = SimulationSampleLibrary.Create(libRoot);
            Console.WriteLine($"Biblioteca de demonstração: {lib.Samples.Count} WAVs, {lib.Presets.Count} presets (pasta temporária)");
            bool ok = Report("Offline", SwapHarness.RunOfflineAsync(lib, o).GetAwaiter().GetResult(), o, outDir);
            if (realtime)
            {
                Console.WriteLine();
                Console.WriteLine($"Rodando em tempo real (velocidade {speed.ToString("0.#", Inv)}x)...");
                ok &= Report("Tempo real", RunRealtimeAsync(lib, o, speed).GetAwaiter().GetResult(), o, Path.Combine(outDir, "tempo-real"));
            }
            Console.WriteLine();
            Console.WriteLine(ok ? "RESULTADO: OK" : "RESULTADO: FALHOU");
            return ok ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Erro: {UserMessages.FileError(ex)}");
            return 2;
        }
        finally
        {
            try { Directory.Delete(libRoot, true); } catch { /* best effort */ }
        }
    }

    private static bool Report(string title, SwapRecording rec, SwapOptions o, string dir)
    {
        var rep = SwapHarness.Analyze(rec, o);
        var files = SwapHarness.WriteFiles(rec, rep, dir);
        Console.WriteLine($"== {title} — {(rep.Passed ? "OK" : "FALHOU")} ({rec.Mode})");
        foreach (var op in rec.Operations)
            Console.WriteLine($"  operação {op.Step}: {op.Description} em {op.RequestedAtSec.ToString("0.000", Inv)} s -> {op.Result.Status}" +
                              (op.Result.Errors.Count == 0 ? "" : " (" + string.Join("; ", op.Result.Errors.Select(e => $"{e.Piece.Label()} {e.Code}")) + ")"));
        foreach (var c in rep.Checks)
        {
            Console.WriteLine($"  [{(c.Passed ? "OK" : "FALHA")}] {c.Name}");
            Console.WriteLine($"         {c.Detail}");
        }
        Console.WriteLine($"  Arquivos em {Path.GetFullPath(dir)}: {string.Join(", ", files.Select(Path.GetFileName))}");
        return rep.Passed;
    }

    /// <summary>
    /// Same timeline through the running <see cref="FakeAudioHost"/>: operations are issued at the audio positions while
    /// the host keeps processing (they complete asynchronously, as in real use); the host records the kit of every block.
    /// </summary>
    public static async Task<SwapRecording> RunRealtimeAsync(SimulationSampleLibrary lib, SwapOptions o, double speed)
    {
        var sc = KitScenario.Generate(o.Seed);
        var engine = SwapHarness.CreateEngine(sc);
        await using var controller = new KitController(engine, new SampleLibrary(lib.SamplesFolder));
        var initial = await controller.ApplyPreset(lib.Preset(o.InitialPresetId)).Completion;
        if (initial.Status != KitOperationStatus.Applied)
            throw new InvalidOperationException("Preset inicial falhou: " + string.Join("; ", initial.Errors.Select(e => e.Message)));

        var routing = RoutingValidator.Validate(RoutingProfiles.Simulation).Resolve();
        using var host = new FakeAudioHost(engine, routing, new FakeHostOptions
        {
            Source = SimulationSource.SyntheticKit, Seed = o.Seed, BlockSize = o.BlockSize, Loop = false,
            CaptureSeconds = sc.Length / (double)sc.SampleRate + 1, Speed = speed,
        }) { RecordKitPerBlock = engine };
        string? fault = null;
        bool stoppedEarly = false;
        host.Faulted += m => fault = m;
        var events = new List<KitEvent>();
        var plan = SwapHarness.Plan(lib, o).ToList();
        var issued = new List<(SwapHarness.PlannedStep Step, long Block, PreparedKit? Before, KitOperation Op, Task<PreparedKit?> After)>();
        host.Start();
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(sc.Length / (double)sc.SampleRate / speed + 30);
        int next = 0;
        while (host.IsRunning && DateTime.UtcNow < limit)
        {
            long pos = host.PositionSamples;
            while (next < plan.Count && pos >= (long)(plan[next].AtSec * sc.SampleRate))
            {
                var step = plan[next++];
                var before = engine.ActiveKit;
                var op = step.Issue(controller);
                // Kit in effect right after this operation finished (operations are seconds apart).
                var after = op.Completion.ContinueWith(_ => engine.ActiveKit, TaskScheduler.Default);
                issued.Add((step, pos / o.BlockSize, before, op, after));
            }
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
            Thread.Sleep(2);
        }
        stoppedEarly = host.IsRunning; // timed out
        long callbacks = host.CallbackCount;
        host.Stop();
        while (engine.TryDequeueEvent(out var e)) events.Add(e);
        var cap = host.GetCapture() ?? throw new InvalidOperationException("Sem captura dos retornos.");
        var rec = host.GetBlockRecord() ?? throw new InvalidOperationException("Sem registro por bloco.");
        var ops = new List<SwapOperationRecord>();
        foreach (var x in issued)
        {
            var result = await x.Op.Completion;
            ops.Add(new SwapOperationRecord(x.Step.Step, x.Step.Description, x.Step.AtSec, x.Block, result, x.Before, await x.After));
        }
        var byPiece = cap.ByPiece.Select(a => a.Length >= sc.Length ? a[..sc.Length] : a.Concat(new float[sc.Length - a.Length]).ToArray()).ToArray();
        return new SwapRecording
        {
            Scenario = sc, Mode = $"tempo real (FakeAudioHost, velocidade {speed.ToString("0.#", Inv)}x; operações enquanto o áudio roda)",
            BlockSize = o.BlockSize, ByPiece = byPiece, Events = events, BlockKits = rec.Kits, BlockGenerations = rec.Generations,
            Operations = ops, Callbacks = callbacks, HostInterrupted = fault is not null || stoppedEarly,
            HostNote = fault ?? (stoppedEarly ? "não terminou no tempo esperado" : null),
        };
    }

    private static int Int(string option, string value) =>
        int.TryParse(value, NumberStyles.Integer, Inv, out var v) ? v
            : throw new ArgumentException($"{option}: valor inválido \"{value}\" (esperado um número inteiro).");
}
