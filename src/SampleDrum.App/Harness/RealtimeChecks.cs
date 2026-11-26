using SampleDrum.App.Audio;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Harness;

/// <summary>
/// Checks that need the Windows simulation host (<see cref="FakeAudioHost"/>): the real-time run matches the scenario's
/// expectations, and swapping the kit (<see cref="KitEngine.ApplyKit"/>) while the host runs is glitch-free.
/// Used by <c>--selftest</c> and tests/SampleDrum.App.Tests.
/// </summary>
public static class RealtimeChecks
{
    public static IReadOnlyList<CheckResult> RunAll(double speed = 4.0) =>
    [
        HarnessChecks.Safe($"Simulação em tempo real (FakeAudioHost, bloco 128, velocidade {speed:0.#}x) bate com o gabarito",
            () => RealtimeMatchesScenario(128, speed)),
        HarnessChecks.Safe("Troca de kit (ApplyKit) com a simulação rodando", () => ApplyKitWhileRunning(speed)),
    ];

    /// <summary>Runs the synthetic scenario through FakeAudioHost (no loop, capture on) and evaluates it.</summary>
    /// <param name="source">Source part of the host options (Source, Seed, MicPaths, MultichannelPath/Channels); it
    /// must describe the same signals as <paramref name="sc"/>.</param>
    public static HarnessResult RunRealtime(KitScenario sc, FakeHostOptions source, int block, double speed, bool preview,
        Action<string>? log = null)
    {
        var o = new HarnessOptions { BlockSize = block };
        var validation = RoutingValidator.Validate(o.Profile);
        var routing = validation.Resolve();
        var engine = KitHarness.CreateEngine(sc);
        var hostOptions = source with
        {
            BlockSize = block,
            Loop = false,
            CaptureSeconds = sc.Length / (double)sc.SampleRate + 1,
            PreviewEnabled = preview,
            Preview = new PreviewMixSettings { MicLevelDb = -18f },
            Speed = speed,
        };
        var events = new List<KitEvent>();
        string? fault = null;
        using var host = new FakeAudioHost(engine, routing, hostOptions);
        host.Faulted += m => fault = m;
        host.Start();
        if (preview && host.PreviewError is not null) log?.Invoke("AVISO: " + host.PreviewError);
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(sc.Length / (double)sc.SampleRate / speed + 30);
        while (host.IsRunning && DateTime.UtcNow < limit)
        {
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
            Thread.Sleep(20);
        }
        bool timedOut = host.IsRunning;
        host.Stop();
        while (engine.TryDequeueEvent(out var e)) events.Add(e);
        if (preview) log?.Invoke($"Prévia: {host.PreviewUnderruns} faltas de dados (underruns), {host.PreviewOverruns} blocos descartados.");
        if (fault is not null) throw new InvalidOperationException(fault);
        if (timedOut) throw new TimeoutException("A simulação em tempo real não terminou no tempo esperado.");
        var cap = host.GetCapture() ?? throw new InvalidOperationException("Sem captura dos retornos.");
        // Captured length = scenario length rounded up to whole blocks; trim to the scenario.
        var byPiece = cap.ByPiece.Select(a => Fit(a, sc.Length)).ToArray();
        var physical = new SortedDictionary<int, float[]>(cap.Physical.ToDictionary(kv => kv.Key, kv => Fit(kv.Value, sc.Length)));
        return KitHarness.Evaluate(sc, o, validation, routing, byPiece, physical, events, block,
            $"tempo real (FakeAudioHost, velocidade {speed:0.#}x{(preview ? ", com prévia" : "")})");
    }

    private static float[] Fit(float[] a, int len) => a.Length == len ? a : a.Length > len ? a[..len] : a.Concat(new float[len - a.Length]).ToArray();

    public static (bool, string) RealtimeMatchesScenario(int block, double speed)
    {
        var sc = KitScenario.Generate(1);
        var rt = RunRealtime(sc, new FakeHostOptions { Source = SimulationSource.SyntheticKit, Seed = 1 }, block, speed, preview: false);
        var off = KitHarness.RunOffline(sc, new HarnessOptions { BlockSize = block });
        var rtPos = rt.Events.Where(e => !e.Event.IsManualTest).Select(e => (e.Piece, e.Event.SampleIndex)).OrderBy(x => x.SampleIndex).ThenBy(x => x.Piece).ToList();
        var offPos = off.Events.Where(e => !e.Event.IsManualTest).Select(e => (e.Piece, e.Event.SampleIndex)).OrderBy(x => x.SampleIndex).ThenBy(x => x.Piece).ToList();
        bool same = rtPos.SequenceEqual(offPos);
        bool ok = rt.Report.Passed && same;
        return (ok, $"tempo real: {(rt.Report.Passed ? "gabarito OK" : "gabarito FALHOU")}, {rtPos.Count} disparos; " +
                    $"idênticos ao offline (peça e amostra): {same}");
    }

    public static (bool, string) ApplyKitWhileRunning(double speed)
    {
        var sc = KitScenario.Generate(1);
        var routing = RoutingValidator.Validate(RoutingProfiles.Simulation).Resolve();
        var engine = KitHarness.CreateEngine(sc);
        var kitB = new PreparedKit("Kit B (polaridade invertida)", sc.SampleRate, DrumPieces.All.Select(p =>
            new PreparedPiece(p, sc.Samples[(int)p].Select(v => -v).ToArray(), null, "B")));
        var events = new List<KitEvent>();
        string? fault = null;
        double seconds = 6.0;
        using var host = new FakeAudioHost(engine, routing, new FakeHostOptions
        {
            Source = SimulationSource.SyntheticKit, Seed = 1, BlockSize = 256, Loop = true, CaptureSeconds = seconds, Speed = speed,
        });
        host.Faulted += m => fault = m;
        host.Start();
        // Swap at ~3.0 s of audio (between the soft kick at 2.5 s and the block-crossing kick at 4.8 s).
        long swapTarget = (long)(3.0 * sc.SampleRate);
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(seconds / speed + 20);
        while (host.PositionSamples < swapTarget && DateTime.UtcNow < limit) Thread.Sleep(1);
        long swapPos = host.PositionSamples;
        engine.ApplyKit(kitB);
        while (host.PositionSamples < (long)(seconds * sc.SampleRate) && host.IsRunning && DateTime.UtcNow < limit)
        {
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
            Thread.Sleep(10);
        }
        host.Stop();
        while (engine.TryDequeueEvent(out var e)) events.Add(e);
        if (fault is not null) return (false, fault);
        var cap = host.GetCapture();
        if (cap is null) return (false, "sem captura");
        var kickOut = cap.Value.ByPiece[0];
        var a = sc.Samples[0];
        int w = sc.SampleRate / 200;
        double Corr(long at)
        {
            double s = 0;
            for (int k = 0; k < w && at + k < kickOut.Length; k++) s += kickOut[at + k] * a[k];
            return s;
        }
        var kicks = events.Where(e => e.Piece == DrumPiece.Kick && !e.Event.IsManualTest && e.Event.PlaybackSampleIndex + w < cap.Value.Frames).ToList();
        var before = kicks.Where(e => e.Event.PlaybackSampleIndex + w < swapPos).ToList();
        var after = kicks.Where(e => e.Event.PlaybackSampleIndex > swapPos + 2 * 256).ToList();
        bool ok = before.Count > 0 && after.Count > 0
                  && before.All(e => Corr(e.Event.PlaybackSampleIndex) > 0)
                  && after.All(e => Corr(e.Event.PlaybackSampleIndex) < 0)
                  && ReferenceEquals(engine.ActiveKit, kitB);
        return (ok, $"troca em {swapPos / (double)sc.SampleRate:0.000} s; bumbos antes (kit A): {before.Count}, depois (kit B): {after.Count}; " +
                    $"sinais corretos: {ok}; {host.CallbackCount} blocos sem falha");
    }
}
