using NAudio.Wave.Asio;
using SampleDrum.App.Audio;
using SampleDrum.Core;

namespace SampleDrum.App;

/// <summary>
/// Headless smoke test (no hardware): FakeAudioHost + TriggerEngine in real time, plus a round-trip check of the
/// native ASIO sample converters. Exit 0 = pass.
/// </summary>
internal static class SelfTest
{
    public static int Run(double enabledSeconds)
    {
        const int rate = 48000;
        bool ok = true;
        Console.WriteLine("SampleDrum --selftest (sem hardware)");

        ok &= CheckConverters();

        var engine = new TriggerEngine(rate, TriggerSettings.Default);
        engine.Enabled = false;
        engine.SetSample(SyntheticSample(rate));

        using var host = new FakeAudioHost(engine, rate);
        string? fault = null;
        host.Faulted += m => fault = m;
        host.Start();
        Console.WriteLine($"Host: {host.Description}");

        // Phase 1: disabled -> no automatic events expected.
        Thread.Sleep(1000);
        var (autoDisabled, manualDisabled) = Drain(engine);
        long kicksPhase1 = host.KicksGenerated;

        // Phase 2: enabled, plus one manual test in the middle.
        engine.Enabled = true;
        Thread.Sleep(TimeSpan.FromSeconds(enabledSeconds / 2));
        engine.RequestTest();
        Thread.Sleep(TimeSpan.FromSeconds(enabledSeconds / 2));
        host.Stop();
        var (autoEnabled, manualEnabled) = Drain(engine);
        long kicksPhase2 = host.KicksGenerated - kicksPhase1;

        double totalSec = 1.0 + enabledSeconds;
        long expectedBlocks = (long)(totalSec * rate / host.FramesPerBuffer);
        long blocks = host.CallbackCount;

        Console.WriteLine($"Blocos processados: {blocks} (esperado ~{expectedBlocks})");
        Console.WriteLine($"Fase desativada: {kicksPhase1} bumbos sintéticos, {autoDisabled} eventos automáticos, {manualDisabled} testes");
        Console.WriteLine($"Fase ativada: {kicksPhase2} bumbos sintéticos, {autoEnabled} eventos automáticos, {manualEnabled} testes");
        Console.WriteLine($"Eventos totais: {autoDisabled + manualDisabled + autoEnabled + manualEnabled}");
        Console.WriteLine($"Amostras de saída não silenciosas: {host.OutputActiveSamples}");

        ok &= Check(fault is null, $"sem falha do host{(fault is null ? "" : ": " + fault)}");
        ok &= Check(blocks > expectedBlocks * 0.8 && blocks < expectedBlocks * 1.2, "contagem de blocos em tempo real (±20%)");
        ok &= Check(autoDisabled == 0, "nenhum disparo automático com o disparo DESATIVADO");
        ok &= Check(autoEnabled >= 1 && autoEnabled <= kicksPhase2 + 1, "disparos automáticos com o disparo ATIVADO");
        ok &= Check(manualEnabled == 1, "\"Testar sample\" gera exatamente 1 evento");
        ok &= Check(host.OutputActiveSamples > 0, "saída com áudio do sample");

        Console.WriteLine(ok ? "RESULTADO: OK" : "RESULTADO: FALHOU");
        return ok ? 0 : 1;
    }

    private static (int auto, int manual) Drain(TriggerEngine engine)
    {
        int auto = 0, manual = 0;
        while (engine.TryDequeueEvent(out var e))
        {
            if (e.IsManualTest) manual++; else auto++;
        }
        return (auto, manual);
    }

    private static float[] SyntheticSample(int rate)
    {
        var s = new float[rate / 5];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)rate;
            s[i] = 0.8f * MathF.Sin(2f * MathF.PI * 60f * t) * MathF.Exp(-t / 0.05f);
        }
        return s;
    }

    private static bool CheckConverters()
    {
        bool ok = true;
        float[] src = [0f, 0.5f, -0.5f, 0.999f, -1f, 1.5f, -1.5f, 0.123456f];
        foreach (var type in new[] { AsioSampleType.Int32LSB, AsioSampleType.Float32LSB, AsioSampleType.Int16LSB, AsioSampleType.Int24LSB })
        {
            var bytes = new byte[src.Length * NativeSampleConverter.BytesPerSample(type)];
            var back = new float[src.Length];
            NativeSampleConverter.FromFloat(src, type, bytes);
            NativeSampleConverter.ToFloat(bytes, type, back);
            float tol = type == AsioSampleType.Int16LSB ? 1e-4f : 1e-6f;
            bool pass = true;
            for (int i = 0; i < src.Length; i++)
            {
                float expected = Math.Clamp(src[i], -1f, 1f);
                if (MathF.Abs(back[i] - expected) > tol) pass = false;
            }
            ok &= Check(pass, $"conversão float <-> {type} (com saturação)");
        }
        return ok;
    }

    private static bool Check(bool condition, string what)
    {
        Console.WriteLine($"  [{(condition ? "OK" : "FALHA")}] {what}");
        return condition;
    }
}
